using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace World_Generator.Chunk_Generation.Layers.Internal
{
    [BurstCompile]
    public struct PrefetchGreedyMeshDataJob : IJobParallelFor
    {
        [ReadOnly] public NativeHashMap<byte, BlockData> BlockMap;
        
        [NativeDisableParallelForRestriction] 
        public NativeArray<byte> Blocks;
        [NativeDisableParallelForRestriction] 
        public NativeArray<byte> LightLevels;

        [NativeDisableParallelForRestriction] 
        public NativeArray<ChunkMesher.MeshDataOffsets> ChunkOffsets;
             
        [NativeDisableParallelForRestriction] 
        public NativeArray<int> LODs;

        [ReadOnly] public ChunkSize ChunkSize;
        
        [ReadOnly] public int BlockPadding;

        private int _chunkIndex;
        private int _chunkStartIndex;
        private int _blocksPerChunk;
        private int _paddedWidth;

        private int _normOffset;

        private int _stepSize;

        public void Execute(int index)
        {
            _chunkIndex = index;

            _stepSize = LODs[_chunkIndex];
            ChunkMesher.MeshDataOffsets offsets = ChunkOffsets[_chunkIndex];
            
            _paddedWidth = ChunkSize.GetPaddedWidth(BlockPadding);
            _blocksPerChunk = ChunkSize.GetTotalSize(BlockPadding);
            _chunkStartIndex = _chunkIndex * _blocksPerChunk;

            // Process each of the 6 faces separately (greedy meshing works on 2D slices)
            // We'll do: Right(+X), Left(-X), Top(+Y), Bottom(-Y), Front(+Z), Back(-Z)
            GreedyMeshFace(new int3(1, 0, 0), ref offsets);  // Right face (+X)
            GreedyMeshFace(new int3(-1, 0, 0), ref offsets); // Left face (-X)

            GreedyMeshFace(new int3(0, 1, 0), ref offsets);  // Top face (+Y)
            GreedyMeshFace(new int3(0, -1, 0), ref offsets); // Bottom face (-Y)
            GreedyMeshFace(new int3(0, 0, 1), ref offsets);  // Front face (+Z)
            GreedyMeshFace(new int3(0, 0, -1), ref offsets); // Back face (-Z)

            ChunkOffsets[_chunkIndex] = offsets;
        }
        
        /// <summary>
        /// Performs greedy meshing for a single face direction
        /// </summary>
        /// <param name="normal">The normal vector of the face we're processing</param>
        private void GreedyMeshFace(int3 normal, ref ChunkMesher.MeshDataOffsets _offsets)
        {

            // Determine which axis is perpendicular to the face (the "depth" axis)
            // and which two axes form the plane of the face (u and v axes)
            int3 axisU, axisV;
            int dimU, dimV, dimDepth; // Dimensions along each axis
        
            if (normal.x != 0) // Right or Left face (YZ plane)
            {
                axisU = new int3(0, 1, 0);  // U axis is Y
                axisV = new int3(0, 0, 1);  // V axis is Z
                dimU = ChunkSize.Height;
                dimV = ChunkSize.Width;
                dimDepth = ChunkSize.Width;
            }
            else if (normal.y != 0) // Top or Bottom face (XZ plane)
            {
                axisU = new int3(1, 0, 0);  // U axis is X
                axisV = new int3(0, 0, 1);  // V axis is Z
                dimU = ChunkSize.Width;
                dimV = ChunkSize.Width;
                dimDepth = ChunkSize.Height;
            }
            else // Front or Back face (XY plane)
            {
                axisU = new int3(1, 0, 0);  // U axis is X
                axisV = new int3(0, 1, 0);  // V axis is Y
                dimU = ChunkSize.Width;
                dimV = ChunkSize.Height;
                dimDepth = ChunkSize.Width;
            }
            int maskDimU = (dimU + _stepSize - 1) / _stepSize;
            int maskDimV = (dimV + _stepSize - 1) / _stepSize;

            NativeArray<FaceMaskEntry> mask =
                new NativeArray<FaceMaskEntry>(
                    maskDimU * maskDimV,
                    Allocator.Temp);
            
            // Iterate through each slice perpendicular to the normal direction
            for (int depth = 0; depth < dimDepth; depth+= _stepSize)
            {
                // Clear the mask for this slice
                for (int i = 0; i < mask.Length; i++)
                    mask[i] = new FaceMaskEntry{ BlockType = 0 };

                // Build the mask: determine which blocks need a face rendered
                for (int cv = 0; cv < maskDimV; cv++)
                {
                    for (int cu = 0; cu < maskDimU; cu++)
                    {
                        int u = cu * _stepSize;
                        int v = cv * _stepSize;
                        
                        // Calculate actual position in chunk space
                        int3 pos = GetPositionFromUVD(u, v, depth, normal, axisU, axisV);
                        byte blockType = FindCoarseFaceBlock(pos, normal);

                        if (blockType != 0)
                        {
                            uint light =
                                SampleFaceCornerLight(
                                    pos,
                                    normal,
                                    axisU,
                                    axisV);

                            mask[cu + cv * maskDimU] =
                                new FaceMaskEntry
                                {
                                    BlockType = blockType,
                                    PackedCornerLight = light
                                };
                        }
                    }
                }
                
                // Greedy meshing: merge adjacent faces in the mask
                for (int cv = 0; cv < maskDimV; cv ++)
                {
                    for (int cu = 0; cu < maskDimU; cu ++)
                    {
                        int maskIndex = cu + cv * maskDimU;
                        
                        var maskEntry = mask[maskIndex];
                        
                        if (maskEntry.BlockType == 0) continue; // Already processed or empty

                        // Determine width: how far can we extend in U direction
                        int width = 1;
                        while (cu + width < maskDimU)
                        {
                            var other = mask[cu + width + cv * maskDimU];
                            if (other.BlockType != maskEntry.BlockType ||
                                other.PackedCornerLight != maskEntry.PackedCornerLight)
                                break;
                            
                            width++;
                        }
                        
                        // Determine height: how far can we extend in V direction
                        int height = 1;
                        bool canExtendHeight = true;
                        
                        while (cv + height < maskDimV && canExtendHeight)
                        {
                            // Check if the entire row matches
                            for (int k = 0; k < width; k++)
                            {
                                var other = mask[cu + k + (cv + height) * maskDimU];
                                
                                if (other.BlockType != maskEntry.BlockType ||
                                    other.PackedCornerLight != maskEntry.PackedCornerLight)
                                {
                                    canExtendHeight = false;
                                    break;
                                }
                            }
                            if (canExtendHeight) height++;
                        }
                        
                        // Create the merged quad
                        _offsets.VertexCount += 4;
                        _offsets.TriangleCount += 6;
                        
                        // Clear the mask for all blocks we just merged
                        for (int h = 0; h < height; h++)
                        {
                            for (int w = 0; w < width; w++)
                            {
                                mask[cu + w + (cv + h) * maskDimU] = new FaceMaskEntry{ BlockType = 0 };
                            }
                        }
                    }
                }
            }
            
            mask.Dispose();
        }
        
        /// <summary>
        /// Converts U, V, Depth coordinates to actual chunk position based on face orientation
        /// </summary>
        private int3 GetPositionFromUVD(
            int u,
            int v,
            int depth,
            int3 normal,
            int3 axisU,
            int3 axisV)
        {
            int3 pos = new int3(BlockPadding, 0, BlockPadding);

            if (normal.x != 0)
            {
                pos.x = normal.x > 0
                    ? BlockPadding + depth
                    : _paddedWidth - BlockPadding - 1 - depth;

                pos.y = u;
                pos.z = BlockPadding + v;
            }
            else if (normal.y != 0)
            {
                pos.x = BlockPadding + u;
                pos.y = normal.y > 0
                    ? depth
                    : ChunkSize.Height - 1 - depth;

                pos.z = BlockPadding + v;
            }
            else
            {
                pos.x = BlockPadding + u;
                pos.y = v;
                pos.z = normal.z > 0
                    ? BlockPadding + depth
                    : _paddedWidth - BlockPadding - 1 - depth;
            }

            // For negative-facing coarse cells we sampled the far voxel.
            // Move back to the first voxel represented by this coarse cell.
            if (normal.x < 0 || normal.y < 0 || normal.z < 0)
                pos += normal * (_stepSize - 1);

            return pos;
        }
        
        /// <summary>
        /// Gets block type at position, using padding for neighbor checks
        /// </summary>
        private byte GetBlockAt(int3 pos)
        {
            // Check if position is out of bounds (below 0 or above chunk height)
            if (pos.y < 0 || pos.y >= ChunkSize.Height)
                return 0; // Air
            
            // For X and Z, the padding contains neighbor chunk data, so don't clamp
            // Just make sure we're within the padded area
            if (pos.x < 0 || pos.x >= _paddedWidth || pos.z < 0 || pos.z >= _paddedWidth)
                return 0;
            
            int index = pos.x + pos.z * _paddedWidth + pos.y * _paddedWidth * _paddedWidth;
            return Blocks[_chunkStartIndex + index];
        }
        
        private byte GetLightAt(int3 pos)
        {
            // Check if position is out of bounds (below 0 or above chunk height)
            if (pos.y < 0 || pos.y >= ChunkSize.Height)
                return 0; // Air
            
            // For X and Z, the padding contains neighbor chunk data, so don't clamp
            // Just make sure we're within the padded area
            if (pos.x < 0 || pos.x >= _paddedWidth || pos.z < 0 || pos.z >= _paddedWidth)
                return 0;
            
            int index = pos.x + pos.z * _paddedWidth + pos.y * _paddedWidth * _paddedWidth;
            return LightLevels[_chunkStartIndex + index];
        }

        private uint SampleFaceCornerLight(
            int3 pos,
            int3 normal,
            int3 axisU,
            int3 axisV)
        {
            int3 basePos;

            if (normal.x > 0 || normal.y > 0 || normal.z > 0)
                basePos = pos + normal * _stepSize;
            else
                basePos = pos + normal;

            int3 uStep = axisU * _stepSize;
            int3 vStep = axisV * _stepSize;

            byte l0 = GetLightAt(basePos);
            byte l1 = GetLightAt(basePos + vStep);
            byte l2 = GetLightAt(basePos + uStep + vStep);
            byte l3 = GetLightAt(basePos + uStep);

            return (uint)(
                l0 |
                (l1 << 8) |
                (l2 << 16) |
                (l3 << 24));
        }
        private byte FindCoarseFaceBlock(
            int3 pos,
            int3 normal)
        {
            byte blockType = 0;

            // Current coarse voxel
            for (int z = 0; z < _stepSize; z++)
            {
                for (int y = 0; y < _stepSize; y++)
                {
                    for (int x = 0; x < _stepSize; x++)
                    {
                        int3 voxel = pos + new int3(x, y, z);

                        byte type = GetBlockAt(voxel);

                        if (type != 0 &&
                            BlockMap[type].IsSolid)
                        {
                            blockType = type;
                            break;
                        }
                    }

                    if (blockType != 0)
                        break;
                }

                if (blockType != 0)
                    break;
            }

            if (blockType == 0)
                return 0;

            // Neighbor coarse voxel
            int3 neighborPos = pos + normal * _stepSize;

            for (int z = 0; z < _stepSize; z++)
            {
                for (int y = 0; y < _stepSize; y++)
                {
                    for (int x = 0; x < _stepSize; x++)
                    {
                        int3 voxel =
                            neighborPos + new int3(x, y, z);

                        byte type = GetBlockAt(voxel);

                        if (type != 0 &&
                            BlockMap[type].IsSolid)
                        {
                            return 0; // Neighbor coarse voxel is occupied.
                        }
                    }
                }
            }

            return blockType;
        }
    }
}