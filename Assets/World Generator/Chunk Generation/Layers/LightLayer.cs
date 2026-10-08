using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace World_Generator.Chunk_Generation.Layers
{
    
    public class LightLayer : IGeneratorLayer
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
            var job = new LightLayerJob
            {
                Blocks = buffers.Blocks,
                LightLevels = buffers.LightLevels,
                ChunkSize = settings.ChunkSize,
                BlockPadding = GeneratorConstants.BLOCK_PADDING
            };

            return job.Schedule(
                buildParams.RequestBatch.Count, 
                settings.TasksPerThread,
                dependency
            );
        }
    }

    [BurstCompile]
    public struct LightLayerJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] 
        public NativeArray<byte> Blocks;
        [NativeDisableParallelForRestriction] 
        public NativeArray<byte> LightLevels;

        public ChunkSize ChunkSize;
        public int BlockPadding;
        
        
        public void Execute(int chunkIndex)
        {
            int blocksPerChunk = ChunkSize.GetTotalSize(BlockPadding);
            int paddedWidth = ChunkSize.GetPaddedWidth(BlockPadding);
            int chunkBasePadding = blocksPerChunk * chunkIndex;

            
            int sunlight = 15;
            int currentLight = 0;
            bool blocked = false;

            for (var x = 0; x < paddedWidth; x++)
            for (var z = 0; z < paddedWidth; z++)
            {
                blocked = false;
                currentLight = 0;
                
                for (var y = ChunkSize.Height - 1; y >= 0; y--)
                {
                    int index = x + z * paddedWidth + y * paddedWidth * paddedWidth;
                    index = chunkBasePadding + index;

                    if (!blocked && Blocks[index] == (byte)BlockTypes.Air)
                    {
                        LightLevels[index] = (byte)sunlight;
                        currentLight = sunlight;
                    }
                    else
                    {
                        blocked = true;
                        currentLight = 0;
                    
                        LightLevels[index] = (byte)currentLight;
                    }
                }
            }
        }
    }
    
}