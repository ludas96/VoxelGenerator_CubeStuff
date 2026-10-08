using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace World_Generator.Chunk_Generation.Layers.Internal
{
       [BurstCompile]
    public struct PrefetchMeshDataJob : IJobParallelFor
    {
        [ReadOnly] public NativeHashMap<byte, BlockData> BlockMap;
        
        [NativeDisableParallelForRestriction] 
        public NativeArray<byte> Blocks;

        [NativeDisableParallelForRestriction] 
        public NativeArray<ChunkMesher.MeshDataOffsets> ChunkOffsets;

        [ReadOnly] public ChunkSize ChunkSize;
        
        [ReadOnly] public int BlockPadding;
        
        public void Execute(int chunkIndex)
        {
            ChunkMesher.MeshDataOffsets offsets = ChunkOffsets[chunkIndex];
            
            int paddedWidth = ChunkSize.GetPaddedWidth(BlockPadding);
            int blocksPerChunk = ChunkSize.GetTotalSize(BlockPadding);
            int chunkBlockBase = chunkIndex * blocksPerChunk;
            
            for (int y = 0; y < ChunkSize.Height; y++)
            for (int z = BlockPadding; z < paddedWidth - BlockPadding; z++)
            for (int x = BlockPadding; x < paddedWidth - BlockPadding; x++)
            {
                var index = x + z * paddedWidth + y * paddedWidth * paddedWidth;
                index = chunkBlockBase + index;
                
                var blockType = Blocks[index];
            
                if (blockType == (byte)BlockTypes.Air)
                    continue;
                
                if(!BlockMap.ContainsKey(blockType))
                    continue;

                for (int dir = 0; dir < 6; dir++)
                {
                    int3 neighPos = new int3(x, y, z);

                    var dirPos = ChunkJobHelpers.Native_FaceChecks[dir];

                    var neighborPos = new int3(
                        (int)math.floor(neighPos.x + dirPos.x),
                        (int)math.floor(neighPos.y + dirPos.y),
                        (int)math.floor(neighPos.z + dirPos.z)
                    );
                    
                    byte neighBlockType;

                    if (neighborPos.y < 0 || neighborPos.y >= ChunkSize.Height)
                    {
                        neighBlockType = (byte)BlockTypes.Air;
                    }
                    else
                    {
                        var neighIndex = neighborPos.x + neighborPos.z * paddedWidth + neighborPos.y * paddedWidth * paddedWidth;
                        neighIndex = chunkBlockBase + neighIndex;
                        neighBlockType = Blocks[neighIndex];
                    }
                    
                    if(!BlockMap.TryGetValue(neighBlockType, out var neighborBlockData))
                        continue;
                    
                    if (neighborBlockData.IsSolid)
                        continue;
                    
                    offsets.VertexCount += 4;
                    offsets.TriangleCount += 6;
                    offsets.UVCount += 4;
                }
            }

            ChunkOffsets[chunkIndex] = offsets;
        }
    }
}