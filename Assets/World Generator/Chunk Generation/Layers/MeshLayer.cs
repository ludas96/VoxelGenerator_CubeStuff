using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using World_Generator.Chunk_Generation.Layers.Internal;

namespace World_Generator.Chunk_Generation.Layers
{
    public class MeshLayer
    {
        public JobHandle Schedule(
            MesherJobData jobData,
            NativeHashMap<byte, BlockData> blockMap
        )
        {
            var buffers = jobData.Buffers;
            
            // Reset MeshDataOffsets to prevent misreads
            for (int i = 0; i < buffers.MeshDataOffsets.Length; i++)
            {
                buffers.MeshDataOffsets[i] = new ChunkMesher.MeshDataOffsets();
            }
            
            var prefetchMeshDataJob = new PrefetchGreedyMeshDataJob()
            {
                ChunkSize =  jobData.ChunkSize,
                ChunkOffsets = buffers.MeshDataOffsets,
                BlockPadding = GeneratorConstants.BLOCK_PADDING,
                Blocks = buffers.Blocks,
                LightLevels = buffers.LightLevels,
                BlockMap =  blockMap,
                LODs = buffers.LODs
            };
            
            var handle = prefetchMeshDataJob.Schedule(jobData.TotalChunks, jobData.TasksPerThread);
            
            var sumMeshDataJob = new SumMeshDataJob
            {
                BlocksPerChunk = jobData.ChunkSize.GetTotalSize(GeneratorConstants.BLOCK_PADDING),
                ChunkOffsets = buffers.MeshDataOffsets,
                Vertices =  buffers.Vertices,
                Normals = buffers.Normals,
                UVs = buffers.UVs,
                Triangles = buffers.Triangles,
                Colors = buffers.Colors,
            };
            
            handle = sumMeshDataJob.Schedule(handle);
            
            var meshDataJob = new GenerateGreedyMeshJob()
            {
                ChunkSize = jobData.ChunkSize,
                ChunkOffsets = buffers.MeshDataOffsets,
                BlockPadding =  GeneratorConstants.BLOCK_PADDING,
                Blocks = buffers.Blocks,
                BlockMap =  blockMap,
                Vertices = buffers.Vertices,
                Normals = buffers.Normals,
                UVs = buffers.UVs,
                Triangles = buffers.Triangles,
                LightLevels = buffers.LightLevels,
                Colors = buffers.Colors,
                LODs = buffers.LODs
            };
            
            return meshDataJob.Schedule(jobData.TotalChunks, jobData.TasksPerThread, handle);
        }
    }
    [BurstCompile]
    public struct FaceMaskEntry
    {
        public byte BlockType;
        public uint PackedCornerLight;
    }
}