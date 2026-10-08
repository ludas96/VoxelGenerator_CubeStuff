using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using World_Generator.Chunk_Generation.Layers;

namespace World_Generator.Chunk_Generation
{
    [BurstCompile]
    public struct MesherJobData
    {
        public ChunkMesher.MesherBuffers Buffers;
        public int TotalChunks;
        public int TasksPerThread;
        public ChunkSize ChunkSize;
    }
    
    public class MesherRequest
    {
        public ChunkHandle Handle;
        public ChunkData Data;
        public int LOD;
    }

    public class MesherExtractResult
    {
        public ChunkHandle Handle;
    }
    
    // Handles ChunkMesh jobs
    public sealed class ChunkMesher
    {
        public struct ChunkMesherSettings
        {
            public int TasksPerThread;
            public ChunkSize ChunkSize;
        
            public int MaxActiveBatches;
            public int MaxChunksPerBatch;
        }
        
        private BufferPool _bufferPool;
        private NativeHashMap<byte, BlockData> _blockMap;

        private MeshLayer _meshLayer;

        private ChunkMesherSettings _settings;

        private List<MesherBatch> _activeBatches = new();
        
        private Queue<MesherRequest> _requests = new();
        private Queue<MesherExtractResult> _extractResults = new();
        private Queue<MesherBatch> _readyBatches = new();

        private Mesh _discardMesh = new();

        public ChunkMesher(ChunkMesherSettings settings)
        {
            _settings = settings;
            
            _blockMap = new NativeHashMap<byte, BlockData>(World.Instance.BlockDataMap.Count, Allocator.Persistent);
            foreach (var block in World.Instance.BlockDataMap)
            {
                _blockMap[(byte)block.Key] = block.Value;
            }
            
            _bufferPool = new BufferPool();
            _bufferPool.Initialize(
                _settings.MaxChunksPerBatch,
                _settings.MaxActiveBatches,
                _settings.ChunkSize.GetTotalSize(GeneratorConstants.BLOCK_PADDING));
            
            _meshLayer = new MeshLayer();
        }

        public void Add(MesherRequest request)
        {
            _requests.Enqueue(request);
        }
        
        public bool TryGetReadyBatch(out MesherBatch batch)
        {
            batch = default;
            if (_readyBatches.Count == 0) return false;

            batch = _readyBatches.Dequeue();
            return true;
        }

        public bool TryGetMeshed(out MesherExtractResult result)
        {
            result = default;
            if(_extractResults.Count == 0) return false;
            
            result = _extractResults.Dequeue();
            return true;
        }
        
        public void ApplyMeshBatch(MesherBatch batch, Chunk[] chunks)
        {
            var meshes = new Mesh[batch.TotalChunks];
            for (int i = 0; i < batch.TotalChunks; i++)
            {
                if (chunks[i] != null) meshes[i] = chunks[i].Mesh;
                else meshes[i] = _discardMesh;
            }
            
            Mesh.ApplyAndDisposeWritableMeshData(batch.MeshDataArray, meshes, MeshUpdateFlags.DontRecalculateBounds);

            for (int i = 0; i < batch.TotalChunks; i++)
            {
                // Stale chunk
                if (chunks[i] == null)
                {
                    batch.MarkChunkDone();
                    continue;
                }

                var offsets = batch.JobData.Buffers.Offsets[i];
                chunks[i].Mesh.bounds = new Bounds(
                    (offsets.BoundsMin + offsets.BoundsMax) * 0.5f,
                    offsets.BoundsMax - offsets.BoundsMin);

                chunks[i].Setup(batch.ChunkLODs[i]);
                
                _extractResults.Enqueue(new MesherExtractResult()
                {
                    Handle = batch.ChunkHandles[i]
                });
                
                batch.MarkChunkDone();
            }

            _bufferPool.Return(batch.JobData.Buffers);
            _activeBatches.Remove(batch);
        }
        
        private MesherJobData Internal_CreateJobData(MesherBuildParams buildParams)
        {
            int chunkCount = buildParams.RequestBatch.Count;

            var jobData = new MesherJobData()
            {
                Buffers = buildParams.Buffers,
                TotalChunks = chunkCount,
                TasksPerThread = _settings.TasksPerThread,
                ChunkSize = _settings.ChunkSize
            };
            

            for (int i = 0; i < chunkCount; i++)
            {
                var request = buildParams.RequestBatch[i];
                var chunkBuffer = jobData.Buffers;
                chunkBuffer.ChunkPositions[i] = new int3(request.Handle.Position.x, request.Handle.Position.y, request.Handle.Position.z);
                chunkBuffer.LODs[i] = request.LOD;
            }

            return jobData;
        }

        private JobHandle Build(MesherBuildParams buildParams)
        {
            var jobData = Internal_CreateJobData(buildParams);
            int blocksPerChunk = _settings.ChunkSize.GetTotalSize(GeneratorConstants.BLOCK_PADDING);
            int batchSize = buildParams.RequestBatch.Count;
            var meshDataArray = Mesh.AllocateWritableMeshData(batchSize);
            
            for (int i = 0; i < batchSize; i++)
            {
                var chunkData = buildParams.RequestBatch[i].Data;
                NativeArray<byte>.Copy(
                    chunkData.Blocks,
                    0,
                    jobData.Buffers.Blocks,
                    i * blocksPerChunk,
                    blocksPerChunk);

                NativeArray<byte>.Copy(
                    chunkData.LightLevels,
                    0,
                    jobData.Buffers.LightLevels,
                    i * blocksPerChunk,
                    blocksPerChunk);
            }
            
            JobHandle handle = _meshLayer.Schedule(jobData, _blockMap, meshDataArray);

            _activeBatches.Add(new MesherBatch(buildParams.RequestBatch.Count)
            {
                JobData = jobData,
                Handle = handle,
                MeshDataArray = meshDataArray,
                ChunkHandles = buildParams.RequestBatch.Select(x => x.Handle).ToList(),
                ChunkLODs = buildParams.RequestBatch.Select(x => x.LOD).ToList()
            });

            return handle;
        }

        public void Update()
        {
            ProcessActiveBatches();
            ProcessPendingChunks();
        }

        private void ProcessPendingChunks()
        {
            if (_requests.Count <= 0) return;
            
            while (_activeBatches.Count < _settings.MaxActiveBatches && _requests.Count > 0)
            {
                var batch = new List<MesherRequest>();

                for (int i = 0; i < _settings.MaxChunksPerBatch && _requests.Count > 0; i++)
                {
                    batch.Add(_requests.Dequeue());
                }

                Build(new MesherBuildParams() { RequestBatch = batch, Buffers = _bufferPool.Get() });
            }
        }

        private void ProcessActiveBatches()
        {
            for (int i = _activeBatches.Count - 1; i >= 0; i--)
            {
                var batch = _activeBatches[i];
                
                if (batch.IsSent) continue;
                if (!batch.Handle.IsCompleted) continue;

                batch.Handle.Complete();
                
                batch.IsSent = true;
                _readyBatches.Enqueue(batch);

                //_activeBatches.RemoveAt(i);
                
                // TODO: Kolla om detta funkar?
            }
        }
        
        public void Dispose()
        {
            foreach (var activeBatch in _activeBatches)
            {
                activeBatch.Handle.Complete();
                _bufferPool.Return(activeBatch.JobData.Buffers);
            }
            
            _meshLayer.Dispose();

            _activeBatches.Clear();

            if (_blockMap.IsCreated)
                _blockMap.Dispose();

            _bufferPool.Dispose();
        }

        [BurstCompile]
        public struct MeshDataOffsets
        {
            public int VertexCount;
            public int TriangleCount;
            public int UVCount;

            public float3 BoundsMin;
            public float3 BoundsMax;
        }
        
        private struct MesherBuildParams
        {
            public List<MesherRequest> RequestBatch;
            public MesherBuffers Buffers;
        }
        
        public class MesherBatch
        {
            public MesherJobData JobData;
            public JobHandle Handle;

            public Mesh.MeshDataArray MeshDataArray;
            
            public List<ChunkHandle> ChunkHandles = new();
            public List<int> ChunkLODs = new();
        
            private int _remainingChunks;
        
            public int TotalChunks => JobData.TotalChunks;
        
            public bool IsSent;

            public MesherBatch(int chunkCount)
            {
                _remainingChunks = chunkCount;
            }

            public void MarkChunkDone()
            {
                _remainingChunks--;
            }
        }

        public class MesherBuffers : IDisposable
        {
            public NativeArray<int3> ChunkPositions;
            
            public NativeArray<MeshDataOffsets> Offsets;
            
            public NativeArray<byte> Blocks;
            public NativeArray<byte> LightLevels;

            public NativeArray<int> LODs;

            public void Dispose()
            {
                ChunkPositions.Dispose();
                Offsets.Dispose();
                
                Blocks.Dispose();
                LightLevels.Dispose();

                LODs.Dispose();
            }
        }

        private class BufferPool : IDisposable
        {
            private Queue<MesherBuffers> _buffers = new();

            public void Initialize(int maxChunks, int maxBatches, int maxBlocks)
            {
                int maxBlocksPerChunks = maxChunks * maxBlocks;
                for (int i = 0; i < maxBatches; i++)
                {
                    Return(new MesherBuffers()
                    {
                        ChunkPositions = new NativeArray<int3>(maxChunks, Allocator.Persistent),
                        LODs = new NativeArray<int>(maxChunks, Allocator.Persistent),
                        Offsets = new NativeArray<MeshDataOffsets>(maxChunks, Allocator.Persistent),
                        
                        Blocks = new  NativeArray<byte>(maxBlocksPerChunks, Allocator.Persistent),
                        LightLevels = new NativeArray<byte>(maxBlocksPerChunks, Allocator.Persistent),
                    });
                }
            }

            public void Return(MesherBuffers buffer)
            {
                _buffers.Enqueue(buffer);
            }

            public MesherBuffers Get()
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