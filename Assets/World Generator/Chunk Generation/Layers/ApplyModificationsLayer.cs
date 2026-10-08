using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace World_Generator.Chunk_Generation.Layers
{
    
    public class ApplyModificationsLayer : IGeneratorLayer
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
            var job = new ApplyModificationsJob
            {
                Blocks = buffers.Blocks,
                EditCounts =  buffers.EditCounts,
                EditOffsets = buffers.EditOffsets,
                Edits = buffers.BlockEdits,
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
    public struct ApplyModificationsJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] 
        public NativeArray<byte> Blocks;
        
        [NativeDisableParallelForRestriction] 
        public NativeArray<int> EditOffsets;
        
        [NativeDisableParallelForRestriction] 
        public NativeArray<int> EditCounts;
        
        
        [NativeDisableParallelForRestriction] 
        public NativeArray<ChunkEdit> Edits;

        public ChunkSize ChunkSize;
        public int BlockPadding;
        
        
        public void Execute(int chunkIndex)
        {
            int offset = EditOffsets[chunkIndex];
            int editCount = EditCounts[chunkIndex];
            int blocksPerChunk = ChunkSize.GetTotalSize(BlockPadding);
            int chunkBasePadding = blocksPerChunk * chunkIndex;

            for (int i = 0; i < editCount; i++)
            {
                var edit = Edits[offset + i];
                Blocks[chunkBasePadding + edit.LocalIndex] = (byte)edit.Block;
            }
        }
    }
    
}