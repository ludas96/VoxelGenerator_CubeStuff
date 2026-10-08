using Unity.Collections;
using Unity.Jobs;

namespace World_Generator.Chunk_Generation.Layers
{
    public interface IGeneratorLayer
    {
        public JobHandle Schedule(
            GeneratorJobData jobData,
            GeneratorBuildParams buildParams, 
            NativeHashMap<byte, BlockData> blockMap,
            ChunkGeneratorSettings settings,
            JobHandle dependency
        );
    }
}