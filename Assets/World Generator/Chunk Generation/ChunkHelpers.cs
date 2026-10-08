using System.Collections.Generic;
using UnityEngine;

namespace World_Generator.Chunk_Generation
{
    public static class ChunkHelpers
    {
        public static Vector3Int GetChunkPosFromWorldPos(Vector3 worldPos, ChunkSize chunkSize)
        {
            return new Vector3Int(
                Mathf.FloorToInt(worldPos.x / chunkSize.Width), 
                0, 
                Mathf.FloorToInt(worldPos.z / chunkSize.Width));
        }

        public static Vector3Int GetBlockPosFromWorldPos(Vector3 worldPos, ChunkSize chunkSize)
        {
            int localX = Mathf.FloorToInt(worldPos.x) % chunkSize.Width; 
            int localY = Mathf.FloorToInt(worldPos.y) % chunkSize.Height; 
            int localZ = Mathf.FloorToInt(worldPos.z) % chunkSize.Width; 
            
            if(localX < 0) localX += chunkSize.Width;
            if(localY < 0) localY += chunkSize.Height;
            if(localZ < 0) localZ += chunkSize.Width;
            
            return new Vector3Int(localX, localY, localZ);
        }

        public static int GetIndexFromBlockPos(Vector3Int blockPos, ChunkSize chunkSize)
        {
            var paddedWidth = chunkSize.GetPaddedWidth(GeneratorConstants.BLOCK_PADDING);
            return blockPos.x + blockPos.z * paddedWidth + blockPos.y * paddedWidth * paddedWidth;
        }

        public static bool IsEdgeblock(Vector3Int blockPos, ChunkSize chunkSize)
        {
            return    blockPos.x == 0 
                   || blockPos.x == chunkSize.Width - 1 
                   || blockPos.z == 0 
                   || blockPos.z == chunkSize.Width - 1;
        }
        
        public static List<(Vector3Int chunk, Vector3Int block)> GetNeighborChunksBlock(Vector3Int blockPos, Vector3Int chunkPos, ChunkSize chunkSize)
        {
            if(!IsEdgeblock(blockPos, chunkSize)) return  new List<(Vector3Int, Vector3Int)>();
            
            var res = new List<(Vector3Int, Vector3Int)>();

            var neighborChunkPos = chunkPos;
            if (blockPos.x == 0)
            {
                var neighborBlockPos = new Vector3Int(chunkSize.Width + 1, blockPos.y, blockPos.z + GeneratorConstants.BLOCK_PADDING);
                res.Add((neighborChunkPos - new Vector3Int(1, 0, 0), neighborBlockPos));
            }
            else if (blockPos.x == chunkSize.Width - 1)
            {
                var neighborBlockPos = new Vector3Int(0, blockPos.y, blockPos.z + GeneratorConstants.BLOCK_PADDING);
                res.Add((neighborChunkPos + new Vector3Int(1, 0, 0), neighborBlockPos));
            }
            
            if (blockPos.z == 0)
            {
                var neighborBlockPos = new Vector3Int(blockPos.x + GeneratorConstants.BLOCK_PADDING, blockPos.y, chunkSize.Width + 1);
                res.Add((neighborChunkPos - new Vector3Int(0, 0, 1), neighborBlockPos));
            }
            else if (blockPos.z == chunkSize.Width - 1)
            {
                var neighborBlockPos = new Vector3Int(blockPos.x + GeneratorConstants.BLOCK_PADDING, blockPos.y, 0);
                res.Add((neighborChunkPos + new Vector3Int(0, 0, 1), neighborBlockPos));
            }

            return res;

        }
        
        public static List<(Vector3Int chunkPos, int blockIndex)> GetModifiedChunksAndBlockIndex(Vector3 pos, ChunkSize chunkSize)
        {
            var chunkPos = ChunkHelpers.GetChunkPosFromWorldPos(pos, chunkSize);
            var localBlockPos = ChunkHelpers.GetBlockPosFromWorldPos(pos, chunkSize);
            var localBlockIndex = ChunkHelpers.GetIndexFromBlockPos(
                new Vector3Int(localBlockPos.x + 1, localBlockPos.y, localBlockPos.z + 1),
                chunkSize
            );
            
            var res = new  List<(Vector3Int, int)>()
            {
                (chunkPos, localBlockIndex)
            };
            
            var paddedWidth = chunkSize.GetPaddedWidth(GeneratorConstants.BLOCK_PADDING);
            // West edge
            if (localBlockPos.x == 0)
            {
                var neighborChunkPos = chunkPos + new Vector3Int(-1, 0, 0);
                var neighborBlockPos = new Vector3Int(paddedWidth - 1, localBlockPos.y, localBlockPos.z);
                int neighborIndex = GetIndexFromBlockPos(neighborBlockPos, chunkSize);
                res.Add((neighborChunkPos, neighborIndex));
            }
            // East edge
            else if (localBlockPos.x == chunkSize.Width - 1)
            {
                var neighborChunkPos = chunkPos + new Vector3Int(1, 0, 0);
                var neighborBlockPos = new Vector3Int(0, localBlockPos.y, localBlockPos.z);
                int neighborIndex = GetIndexFromBlockPos(neighborBlockPos, chunkSize);
                res.Add((neighborChunkPos, neighborIndex));
            }
            
            // North
            if (localBlockPos.z == 0)
            {
                var neighborChunkPos = chunkPos + new Vector3Int(0, 0, -1);
                var neighborBlockPos = new Vector3Int(localBlockPos.x, localBlockPos.y, paddedWidth - 1);
                int neighborIndex = GetIndexFromBlockPos(neighborBlockPos, chunkSize);
                res.Add((neighborChunkPos, neighborIndex));
            }
            // South
            else if (localBlockPos.z == chunkSize.Width - 1)
            {
                var neighborChunkPos = chunkPos + new Vector3Int(0, 0, 1);
                var neighborBlockPos = new Vector3Int(localBlockPos.x, localBlockPos.y, 0);
                int neighborIndex = GetIndexFromBlockPos(neighborBlockPos, chunkSize);
                res.Add((neighborChunkPos, neighborIndex));
            }

            return res;
            
        }

        public static int GetLODAtDistance(float distance)
        {
            return 1;
            int lod = 0;
            if (distance <= 6)
                lod = 1;
            else lod = 2;
            /*else if (distance <= 13)
                lod = 2;
            else if (distance <= 20)
                lod = 4;
            else
                lod = 8;
*/
            return lod;
        }
    }
}