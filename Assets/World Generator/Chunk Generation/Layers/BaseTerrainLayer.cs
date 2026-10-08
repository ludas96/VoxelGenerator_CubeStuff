using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace World_Generator.Chunk_Generation.Layers
{
    public class BaseTerrainLayer : IGeneratorLayer
    {
        public JobHandle Schedule(
            GeneratorJobData jobData,
            GeneratorBuildParams buildParams,
            NativeHashMap<byte, BlockData> blockMap,
            ChunkGeneratorSettings settings,
            JobHandle dependency
        )
        {
            var buffers = jobData.Buffers;
            var job = new BaseTerrainJob
            {
                Blocks = buffers.Blocks,
                ChunkSize = settings.ChunkSize,
                BlockPadding = GeneratorConstants.BLOCK_PADDING,
                ChunkPositions = buffers.ChunkPositions,
                Noise = jobData.Noise
            };

            return job.Schedule(
                buildParams.RequestBatch.Count, 
                settings.TasksPerThread,
                dependency
            );
        }
    }

    [BurstCompile]
    public struct BaseTerrainJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] 
        public NativeArray<byte> Blocks;
        
        [NativeDisableParallelForRestriction] 
        [ReadOnly] public ChunkSize ChunkSize;
        
        [ReadOnly] public int BlockPadding;
        [ReadOnly] public NativeArray<int3> ChunkPositions;
        
        public FastNoiseLiteStruct Noise;
        private float _noiseStrength;
        
        public void Execute(int chunkIndex)
        {
            int paddedWidth = ChunkSize.GetPaddedWidth(BlockPadding);
            int blocksPerChunk = ChunkSize.GetTotalSize(BlockPadding);
            int chunkBlockBase = chunkIndex * blocksPerChunk;
            
            int3 localPos = ChunkPositions[chunkIndex];
            int3 worldPos = new int3(localPos.x * ChunkSize.Width, 0, localPos.z * ChunkSize.Width);
        
            _noiseStrength = 0.1f;
            
            //int baseHeight = 32;
            //int heightVariation = 16;

            for (var x = 0; x < paddedWidth; x++)
            for (var z = 0; z < paddedWidth; z++)
            {
                var height = Noise.GetNoise(
                    x + worldPos.x,
                    z + worldPos.z
                ) * _noiseStrength;
                
                // // Remap from [-1, 1] to [0, ChunkSize.Height]
                int blockHeight = (int)(math.floor((height + 1f) * 0.5f * ChunkSize.Height));
            
                // Height
                for (var y = 0; y < ChunkSize.Height; y++)
                {
                    var index = x + z * paddedWidth + y * paddedWidth * paddedWidth;
                    index = chunkBlockBase + index;


                    if (y > blockHeight)
                    {
                        Blocks[index] = (byte)BlockTypes.Air;
                    }
                    else if (y == blockHeight)
                        Blocks[index] = (byte)BlockTypes.Dirt_Grass;
                    else if (y >= blockHeight - 4)
                        Blocks[index] = (byte)BlockTypes.Dirt;
                    else
                        Blocks[index] = (byte)BlockTypes.Stone;
                }
                
            }
        }
    }
}