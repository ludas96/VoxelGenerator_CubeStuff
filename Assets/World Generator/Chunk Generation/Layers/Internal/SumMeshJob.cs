using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace World_Generator.Chunk_Generation.Layers.Internal
{
    [BurstCompile]
    public struct SumMeshDataJob : IJob
    {
        [NativeDisableParallelForRestriction] public NativeArray<ChunkMesher.MeshDataOffsets> ChunkOffsets;
        //[WriteOnly] public NativeArray<MeshDataOffsets> Output_ChunkOffsets;

        [ReadOnly] public int BlocksPerChunk;
        
        public NativeList<float3> Vertices;
        public NativeList<float3> Normals;
        public NativeList<int> Triangles;
        public NativeList<float3> UVs;
        public NativeList<float4> Colors;
        
        public void Execute()
        {
            int vertexOffset = 0;
            int triangleOffset = 0;
            int blockOffset = 0;
            for (int i = 0; i < ChunkOffsets.Length; i++)
            {
                var c = ChunkOffsets[i];
                c.VertexOffset = vertexOffset;
                c.IndexOffset = triangleOffset;
                c.BlockOffset = blockOffset;
                c.BlockCount = BlocksPerChunk;

                ChunkOffsets[i] = c;
            
                vertexOffset += c.VertexCount;
                triangleOffset += c.TriangleCount;
                blockOffset += c.BlockCount;
            }

            Vertices.Resize(vertexOffset, NativeArrayOptions.UninitializedMemory);
            Normals.Resize(vertexOffset, NativeArrayOptions.UninitializedMemory);
            Triangles.Resize(triangleOffset, NativeArrayOptions.UninitializedMemory);
            UVs.Resize(vertexOffset, NativeArrayOptions.UninitializedMemory);
            Colors.Resize(vertexOffset, NativeArrayOptions.UninitializedMemory);
        }
    }
}