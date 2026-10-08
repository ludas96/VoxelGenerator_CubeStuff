using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using World_Generator.Chunk_Generation.Layers;
using World_Generator.Chunk_Generation.Layers.Internal;

namespace World_Generator.Chunk_Generation
{
    public static class GeneratorConstants
    {
        public static int BLOCK_PADDING = 1;
    }
    
    public struct ChunkGeneratorSettings
    {
        public ChunkSize ChunkSize;
        public int TasksPerThread;
        public int MaxActiveBatches;
        public int MaxChunksPerBatch;
    }
    public struct GeneratorBuildParams
    {
        public List<ChunkGenerator.GenerationRequest> RequestBatch;
        public GeneratorBuffers Buffers;
    }
    
    [BurstCompile]
    public struct GeneratorJobData
    {
        public GeneratorBuffers Buffers;
        public FastNoiseLiteStruct Noise;
        public int TotalChunks;
    }
    
    public class GeneratorBatch
    {
        public List<ChunkHandle> ChunkHandles;
        
        public GeneratorJobData JobData;
        public JobHandle Handle;
        
        private int _remainingChunks;
        public int TotalChunks => JobData.TotalChunks;
        
        public bool IsDone => _remainingChunks <= 0;
        public bool IsSent;

        public GeneratorBatch(int chunkCount)
        {
            _remainingChunks = chunkCount;
            ChunkHandles = new List<ChunkHandle>();
        }

        public void MarkChunkDone()
        {
            _remainingChunks--;
        }
    }
    

    public class GeneratorBuffers : IDisposable
    {
        public NativeArray<int3> ChunkPositions;
        
        public NativeArray<byte> Blocks;
        public NativeArray<byte> LightLevels;
        
        public NativeArray<int> EditOffsets;
        public NativeArray<int> EditCounts;
        public NativeArray<ChunkEdit> BlockEdits;
     
        
        public void Dispose()
        {
            ChunkPositions.Dispose();
            Blocks.Dispose();
            LightLevels.Dispose();
            EditOffsets.Dispose();
            EditCounts.Dispose();
            BlockEdits.Dispose();
        }
    }
    
    // Handles ChunkData jobs
    public class ChunkGenerator
    {
        public class GenerationRequest
        {
            public ChunkHandle Handle;
            [CanBeNull] public List<ChunkEdit> Edits;
        }

        public class GenerationResult
        {
            public ChunkHandle Handle;
            public ChunkData Data;
        }
        
        private List<IGeneratorLayer> _generatorLayers = new();
        private NativeHashMap<byte, BlockData> _blockMap;
        private ChunkGeneratorSettings _settings;

        private BufferPool _bufferPool = new();

        private List<GeneratorBatch> _activeBatches = new();
        private Queue<GenerationRequest> _requests = new();
        private Queue<GenerationResult> _results = new();

        private FastNoiseLiteStruct _noiseSettings = new();

        public ChunkGenerator(ChunkGeneratorSettings settings)
        {
            _settings = settings;

            _blockMap = new NativeHashMap<byte, BlockData>(World.Instance.BlockDataMap.Count, Allocator.Persistent);
            foreach (var block in World.Instance.BlockDataMap)
            {
                _blockMap[(byte)block.Key] = block.Value;
            }

            _bufferPool.Initialize(
                _settings.MaxChunksPerBatch,
                _settings.MaxActiveBatches,
                _settings.ChunkSize.GetTotalSize(GeneratorConstants.BLOCK_PADDING)
            );
            _noiseSettings.CopyFrom(World.Instance.Noise);
        }

        public void Add(GenerationRequest request)
        {
            _requests.Enqueue(request);
        }

        public bool TryGetCompleted(out GenerationResult result)
        {
            result = default;
            if (_results.Count == 0) return false;
            
            result = _results.Dequeue();
            return true;
        }

        private GeneratorJobData Internal_CreateJobData(GeneratorBuildParams buildParams)
        {
            var blockSizePerChunk = _settings.ChunkSize.GetTotalSize(GeneratorConstants.BLOCK_PADDING);
            int chunkCount = buildParams.RequestBatch.Count;

            var jobData = new GeneratorJobData
            {
                Buffers = buildParams.Buffers,
                Noise = _noiseSettings,
                TotalChunks = chunkCount
            };

            int editCount = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                var request = buildParams.RequestBatch[i];
                var chunkBuffer = jobData.Buffers;
                chunkBuffer.ChunkPositions[i] = new int3(request.Handle.Position.x, request.Handle.Position.y, request.Handle.Position.z);
                chunkBuffer.EditOffsets[i] = editCount;

                if (request.Edits == null || request.Edits.Count == 0)
                {
                    chunkBuffer.EditCounts[i] = 0;
                    continue;
                }

                foreach (var edit in request.Edits)
                {
                    chunkBuffer.BlockEdits[editCount++] = edit;
                }

                chunkBuffer.EditCounts[i] = request.Edits.Count;
            }

            return jobData;
        }

        public ChunkGenerator WithLayers(params IGeneratorLayer[] layers)
        {
            _generatorLayers.Clear();
            _generatorLayers.AddRange(layers);
            return this;
        }

        private JobHandle Build(GeneratorBuildParams buildParams)
        {
            var jobData = Internal_CreateJobData(buildParams);
            JobHandle handle = default;

            foreach (var layer in _generatorLayers)
            {
                handle = layer.Schedule(jobData, buildParams, _blockMap, _settings, handle);
            }

            _activeBatches.Add(new GeneratorBatch(buildParams.RequestBatch.Count)
            {
                JobData = jobData,
                Handle = handle,
                ChunkHandles = buildParams.RequestBatch.Select(x => x.Handle).ToList()
            });

            return handle;
        }

        public void Dispose()
        {
            foreach (var activeBatch in _activeBatches)
            {
                activeBatch.Handle.Complete();
                _bufferPool.Return(activeBatch.JobData.Buffers);
            }

            _activeBatches.Clear();

            if (_blockMap.IsCreated)
                _blockMap.Dispose();

            _bufferPool.Dispose();
        }

        public void Update()
        {
            ProcessActiveBatches();
            ProcessPendingChunks();
        }

        private void ProcessActiveBatches()
        {
            for (int i = _activeBatches.Count - 1; i >= 0; i--)
            {
                var batch = _activeBatches[i];
                if (!batch.Handle.IsCompleted) continue;

                if (!batch.IsSent)
                {
                    batch.Handle.Complete();
                    for (int j = 0; j < batch.TotalChunks; j++)
                    {
                        var pos = batch.JobData.Buffers.ChunkPositions[j];
                        
                        var blockCount = World.Instance.ChunkSize.GetTotalSize(GeneratorConstants.BLOCK_PADDING);
                        var blockOffset = j * blockCount;
                        
                        var blocks = batch.JobData.Buffers.Blocks
                            .GetSubArray(
                                blockOffset, 
                                blockCount);
                        
                        var lightLevels = batch.JobData.Buffers.LightLevels
                            .GetSubArray(
                                blockOffset, 
                                blockCount);
                        
                        batch.MarkChunkDone();
                        
                        _results.Enqueue(new GenerationResult()
                        {
                            Data = new ChunkData()
                            {
                                Blocks = blocks,
                                LightLevels = lightLevels
                            },
                            Handle = batch.ChunkHandles[j]
                        });
                    }
                    batch.IsSent = true;
                    continue;
                }

                if (!batch.IsDone) continue;

                batch.Handle.Complete();
                _activeBatches.RemoveAt(i);

                _bufferPool.Return(batch.JobData.Buffers);

            }
        }

        private void ProcessPendingChunks()
        {
            while (_activeBatches.Count < _settings.MaxActiveBatches && _requests.Count > 0)
            {
                var batch = new List<GenerationRequest>();

                for (int i = 0; i < _settings.MaxChunksPerBatch && _requests.Count > 0; i++)
                {
                    batch.Add(_requests.Dequeue());
                }

                Build(new GeneratorBuildParams { RequestBatch = batch, Buffers = _bufferPool.Get() });
            }
        }


        private class BufferPool : IDisposable
        {
            private Queue<GeneratorBuffers> _buffers = new();

            public void Initialize(int maxChunks, int maxBatches, int maxBlocks)
            {
                var totalBlocksPerChunk = maxChunks * maxBlocks;
                for (int i = 0; i < maxBatches; i++)
                {
                    _buffers.Enqueue(new GeneratorBuffers()
                    {
                        Blocks = new NativeArray<byte>(totalBlocksPerChunk, Allocator.Persistent),
                        LightLevels = new NativeArray<byte>(totalBlocksPerChunk, Allocator.Persistent),
                        BlockEdits = new NativeArray<ChunkEdit>(totalBlocksPerChunk, Allocator.Persistent),
                        EditOffsets = new NativeArray<int>(maxChunks, Allocator.Persistent),
                        EditCounts = new NativeArray<int>(maxChunks, Allocator.Persistent),

                        ChunkPositions = new NativeArray<int3>(maxChunks, Allocator.Persistent),
                    });
                    Debug.Log($"Enqueued buffer #{i + 1}");
                }
            }

            public void Return(GeneratorBuffers buffer)
            {
                _buffers.Enqueue(buffer);
            }

            public GeneratorBuffers Get()
            {
                var buffer = _buffers.Dequeue();
                return buffer;
            }

            public void Dispose()
            {
                foreach (var buffer in _buffers)
                {
                    buffer.Dispose();
                }
            }
        }
    }
}