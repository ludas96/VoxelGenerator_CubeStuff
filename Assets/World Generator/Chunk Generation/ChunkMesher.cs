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
    public struct ChunkMesherSettings
    {
        // TODO: fix
        public int TasksPerThread;
        public ChunkSize ChunkSize;
        
        public int MaxActiveBatches;
        public int MaxChunksPerBatch;
        public int MaxChunkExtractionsPerFrame;
    }

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

    public class MesherResult
    {
        public ChunkHandle Handle;
        public MesherData Data;
        public ChunkMesher.MesherBatch Batch;
        public int LOD;
    }

    public class MesherExtractRequest
    {
        public ChunkHandle Handle;
        public MesherData Data;
        public Chunk Chunk;

        public ChunkMesher.MesherBatch Batch;

        public int LOD;
    }

    public class MesherExtractResult
    {
        public ChunkHandle Handle;
    }

    public class MesherData
    {
        public NativeArray<float3> Vertices;
        public NativeArray<float3> UVs;
        public NativeArray<int> Triangles;
        public NativeArray<float3> Normals;
        public NativeArray<float4> Colors;

        public int VertexCount;
        public int TriangleCount;
        public Bounds Bounds;
    } 
    
    
    // Handles ChunkMesh jobs
    public sealed class ChunkMesher
    {
        private BufferPool _bufferPool;
        private NativeHashMap<byte, BlockData> _blockMap;

        private MeshLayer _meshLayer;

        private ChunkMesherSettings _settings;

        private List<MesherBatch> _activeBatches = new();
        
        private Queue<MesherRequest> _requests = new();
        private Queue<MesherResult> _results = new();
        private Queue<MesherExtractRequest> _extractQueue = new();
        private Queue<MesherExtractResult> _extractResults = new();

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

        public bool TryGetReadyToMesh(out MesherResult result)
        {
            result = default;
            if (_results.Count == 0) return false;

            result = _results.Dequeue();
            return true;
        }

        public bool TryGetMeshed(out MesherExtractResult result)
        {
            result = default;
            if(_extractResults.Count == 0) return false;
            
            result = _extractResults.Dequeue();
            return true;
        }

        public void ApplyMesh(MesherResult result, Chunk chunk)
        {
            _extractQueue.Enqueue(new MesherExtractRequest
            {
                Chunk = chunk,
                Handle = result.Handle,
                Data = result.Data,
                Batch = result.Batch,
                LOD = result.LOD
            });
        }
        
        private MesherJobData Internal_CreateJobData(MesherBuildParams buildParams)
        {
            int chunkCount = buildParams.RequestBatch.Count;
            var noiseSettings = new FastNoiseLiteStruct();
            noiseSettings.CopyFrom(World.Instance.Noise);

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
            
            JobHandle handle = _meshLayer.Schedule(jobData, _blockMap);

            _activeBatches.Add(new MesherBatch(buildParams.RequestBatch.Count)
            {
                JobData = jobData,
                Handle = handle,
                ChunkHandles = buildParams.RequestBatch.Select(x => x.Handle).ToList(),
                ChunkLODs = buildParams.RequestBatch.Select(x => x.LOD).ToList()
            });

            return handle;
        }

        public void Update()
        {
            ProcessActiveBatches();
            ProcessPendingChunks();
            ProcessExtractQueue();
        }

        private void ProcessPendingChunks()
        {
            if (_requests.Count <= 0) return;
            
            // Pending chunks är nya chunks som begärt att bli meshade
            // Här behöver meshern dela upp pending chunks till batches som sedan körs som ett Burst Job
            // batchen läggs in i active batches och Mesh.AllocateWritableMeshData anropas med antalet chunks i batchen
            
            // TODO: MeshDataArray som returneras från Mesh.AllocateWritableMeshData kan skrivas direkt i jobbet, se över detta?
            
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
            if(_activeBatches.Count <= 0) 
                return;
            
            // Active batches är chunks som håller på att räkna ut sin mesh.
            // När en batch är klar (IsDone) anropas Mesh.ApplyAndDisposeWritableMeshData för antalet chunks i batchen
            
            for (int i = _activeBatches.Count - 1; i >= 0; i--)
            {
                var batch = _activeBatches[i];
                if (!batch.Handle.IsCompleted) continue;

                if (!batch.IsSent)
                {
                    batch.Handle.Complete();
                    for (int j = 0; j < batch.TotalChunks; j++)
                    {
                        var offset = batch.JobData.Buffers.MeshDataOffsets[j];
                        _results.Enqueue(new MesherResult
                        {
                            Handle = batch.ChunkHandles[j],
                            Data = new MesherData()
                            {
                                Colors = batch.JobData.Buffers.Colors.AsArray().GetSubArray(offset.VertexOffset, offset.VertexCount),
                                Normals = batch.JobData.Buffers.Normals.AsArray().GetSubArray(offset.VertexOffset, offset.VertexCount),
                                Triangles = batch.JobData.Buffers.Triangles.AsArray().GetSubArray(offset.IndexOffset, offset.TriangleCount),
                                UVs = batch.JobData.Buffers.UVs.AsArray().GetSubArray(offset.VertexOffset, offset.VertexCount),
                                Vertices = batch.JobData.Buffers.Vertices.AsArray().GetSubArray(offset.VertexOffset, offset.VertexCount),
                                
                                VertexCount = offset.VertexCount,
                                TriangleCount = offset.TriangleCount,
                                Bounds =  new Bounds((offset.BoundsMin + offset.BoundsMax) * 0.5f, offset.BoundsMax - offset.BoundsMin)
                            },
                            Batch = batch,
                            LOD = batch.ChunkLODs[j]
                        });
                    }
                    batch.IsSent = true;
                    continue;
                }

                if (!batch.IsDone) continue;

                _activeBatches.RemoveAt(i);
                _bufferPool.Return(batch.JobData.Buffers);
            }
        }
        
        private void ProcessExtractQueue()
        {
            if(_extractQueue.Count <= 0) return;
            
            if (_extractQueue.Count > 0)
            {
                int count = math.min(_extractQueue.Count, _settings.MaxChunkExtractionsPerFrame);
                var mesherRequests = new MesherExtractRequest[count];
                for (int i = 0; i < count; i++)
                {
                    mesherRequests[i] = _extractQueue.Dequeue();
                }
                
                ExtractChunks(mesherRequests);
            }
        }
        
        private void ExtractChunks(MesherExtractRequest[] mesherRequests)
        {
            var meshDataArray = Mesh.AllocateWritableMeshData(mesherRequests.Length);
            var meshes = new Mesh[mesherRequests.Length];
            for (int i = 0; i < mesherRequests.Length; i++)
            {
                var request = mesherRequests[i];
                meshes[i] = request.Chunk.Mesh;

                PopulateMeshData(meshDataArray[i], request);
                
                request.Chunk.Mesh.bounds = request.Data.Bounds;
            }
            
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, meshes, MeshUpdateFlags.DontRecalculateBounds);
            foreach (var request in mesherRequests)
            {
                request.Chunk.Setup(request.LOD);
                _extractResults.Enqueue(new MesherExtractResult{ Handle = request.Handle });
                request.Batch.MarkChunkDone();
            }
        }

        private void PopulateMeshData(Mesh.MeshData meshData, MesherExtractRequest request)
        {
            var buffers = request.Data;

            meshData.SetVertexBufferParams(
                buffers.VertexCount,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, stream: 0),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 3,
                    stream: 1),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, stream: 2),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, stream: 3)
            );

            var vertices = meshData.GetVertexData<float3>(0);
            var sourceVertices = buffers.Vertices;
            NativeArray<float3>.Copy(sourceVertices, vertices);

            var uvs = meshData.GetVertexData<float3>(1);
            var sourceUvs = buffers.UVs;
            NativeArray<float3>.Copy(sourceUvs, uvs);

            var colors = meshData.GetVertexData<float4>(2);
            var sourceColors = buffers.Colors;
            NativeArray<float4>.Copy(sourceColors, colors);

            var normals = meshData.GetVertexData<float3>(3);
            var sourceNormals = buffers.Normals;
            NativeArray<float3>.Copy(sourceNormals, normals);

            meshData.SetIndexBufferParams(
                buffers.TriangleCount,
                IndexFormat.UInt32);

            var indices = meshData.GetIndexData<int>();
            var sourceIndices = buffers.Triangles;
            NativeArray<int>.Copy(sourceIndices, indices);

            meshData.subMeshCount = 1;
            meshData.SetSubMesh(0, new SubMeshDescriptor(
                0,
                buffers.TriangleCount,
                MeshTopology.Triangles));
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

        [BurstCompile]
        public struct MeshDataOffsets
        {
            public int VertexCount;
            public int TriangleCount;
            public int UVCount;
            public int BlockCount;

            public int VertexOffset;
            public int IndexOffset;
            public int BlockOffset;

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
            
            public List<ChunkHandle> ChunkHandles = new();
            public List<int> ChunkLODs = new();
        
            private int _remainingChunks;
            public int RemainingChunks => _remainingChunks;
        
            public int TotalChunks => JobData.TotalChunks;
        
            public bool IsDone => _remainingChunks <= 0;
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
            public int Id;
            
            public NativeArray<int3> ChunkPositions;
            
            public NativeArray<MeshDataOffsets> MeshDataOffsets;
            
            public NativeArray<byte> Blocks;
            public NativeArray<byte> LightLevels;
            
            public NativeList<float3> Vertices;
            public NativeList<float3> UVs;
            public NativeList<int> Triangles;
            public NativeList<float3> Normals;
            public NativeList<float4> Colors;

            public NativeArray<int> LODs;

            public void Dispose()
            {
                ChunkPositions.Dispose();
                MeshDataOffsets.Dispose();
                
                Blocks.Dispose();
                LightLevels.Dispose();
                
                Vertices.Dispose();
                UVs.Dispose();
                Triangles.Dispose();
                Normals.Dispose();
                Colors.Dispose();
            }
            
            public void Reset()
            {
                Vertices.Clear();   
                UVs.Clear();
                Triangles.Clear();
                Normals.Clear();
                Colors.Clear();
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
                        Id = i + 1,
                        ChunkPositions = new NativeArray<int3>(maxChunks, Allocator.Persistent),
                        LODs = new NativeArray<int>(maxChunks, Allocator.Persistent),
                        MeshDataOffsets = new NativeArray<MeshDataOffsets>(maxChunks, Allocator.Persistent),
                        
                        Blocks = new  NativeArray<byte>(maxBlocksPerChunks, Allocator.Persistent),
                        LightLevels = new NativeArray<byte>(maxBlocksPerChunks, Allocator.Persistent),
                        
                        Vertices = new NativeList<float3>(0, Allocator.Persistent),
                        Triangles = new NativeList<int>(0, Allocator.Persistent),
                        UVs = new NativeList<float3>(0, Allocator.Persistent),
                        Colors = new NativeList<float4>(0, Allocator.Persistent),
                        Normals = new NativeList<float3>(0, Allocator.Persistent),
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
                buffer.Reset();
                
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