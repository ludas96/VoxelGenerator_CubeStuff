using System;
using Unity.Collections;

namespace World_Generator.Chunk_Generation
{
    public sealed class ChunkData : IDisposable
    {

        public NativeArray<byte> Blocks;
        public NativeArray<byte> LightLevels;
        
        public int Version { get; private set; }
        
        public void MarkChanged()
        {
            Version ++;
        }
        
        public void Dispose()
        {
            Blocks.Dispose();
            LightLevels.Dispose();
        }
    }
}