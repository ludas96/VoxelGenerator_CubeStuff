using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
using World_Generator.Chunk_Generation.Layers.Internal;

namespace World_Generator.Chunk_Generation.Layers
{
    public class MeshLayer : IDisposable
    {
        private NativeArray<VertexAttributeDescriptor> _vertexLayout;
        public MeshLayer()
        {
            _vertexLayout = new NativeArray<VertexAttributeDescriptor>(4, Allocator.Persistent);
            _vertexLayout[0] =
                new VertexAttributeDescriptor(
                    VertexAttribute.Position, 
                    VertexAttributeFormat.Float32, 
                    3, 
                    stream: 0);
            _vertexLayout[1] = new VertexAttributeDescriptor(
                VertexAttribute.TexCoord0,
                VertexAttributeFormat.Float32,
                3,
                stream: 1);
            _vertexLayout[2] =  new VertexAttributeDescriptor(
                    VertexAttribute.Color,
                    VertexAttributeFormat.Float32, 
                    4, 
                    stream: 2);
            _vertexLayout[3] = new VertexAttributeDescriptor(
                VertexAttribute.Normal,
                VertexAttributeFormat.Float32,
                3,
                stream: 3);
        }
        
        public JobHandle Schedule(
            MesherJobData jobData,
            NativeHashMap<byte, BlockData> blockMap,
            Mesh.MeshDataArray meshDataArray
        )
        {
            var buffers = jobData.Buffers;
            
            // Reset MeshDataOffsets to prevent misreads
            for (int i = 0; i < buffers.Offsets.Length; i++)
                buffers.Offsets[i] = new ChunkMesher.MeshDataOffsets();
            
            var prefetchJob = new PrefetchGreedyMeshDataJob()
            {
                ChunkSize =  jobData.ChunkSize,
                ChunkOffsets = buffers.Offsets,
                BlockPadding = GeneratorConstants.BLOCK_PADDING,
                Blocks = buffers.Blocks,
                LightLevels = buffers.LightLevels,
                BlockMap =  blockMap,
                LODs = buffers.LODs
            };
            
            var handle = prefetchJob.Schedule(
                jobData.TotalChunks, 
                jobData.TasksPerThread);
            
            var meshJob = new GenerateGreedyMeshJob()
            {
                ChunkSize = jobData.ChunkSize,
                ChunkOffsets = buffers.Offsets,
                BlockPadding =  GeneratorConstants.BLOCK_PADDING,
                Blocks = buffers.Blocks,
                BlockMap =  blockMap,
                LightLevels = buffers.LightLevels,
                LODs = buffers.LODs,
                
                MeshDataArray = meshDataArray,
                VertexLayout = _vertexLayout
            };
            
            return meshJob.Schedule(
                jobData.TotalChunks, 
                jobData.TasksPerThread,
                handle);
        }

        public void Dispose()
        {
            if(_vertexLayout.IsCreated)
                _vertexLayout.Dispose();
        }
    }
    [BurstCompile]
    public struct FaceMaskEntry
    {
        public byte BlockType;
        public uint PackedCornerLight;
    }
}