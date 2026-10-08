using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace World_Generator.Chunk_Generation.Layers.Internal
{
    [BurstCompile]
    public struct GenerateGreedyMeshJob : IJobParallelFor
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

        [ReadOnly] public NativeArray<VertexAttributeDescriptor> VertexLayout;
        public Mesh.MeshDataArray MeshDataArray;

       private int _vertOffset;
       private int _triOffset;
        
        [ReadOnly] public ChunkSize ChunkSize;
        [ReadOnly] public int BlockPadding;
        
        private int _chunkIndex;      // Index of the chunk being processed
        private int _chunkStartIndex; // Starting index in Blocks array for this chunk
        private int _paddedWidth;     // Width/depth of chunk including padding on both sides
        
        private float3 _boundsMin;
        private float3 _boundsMax;

        private int _stepSize;

        public void Execute(int index)
        {
            _chunkIndex = index;

            _stepSize = LODs[_chunkIndex];
            
            _boundsMin = new float3(float.MaxValue);
            _boundsMax = new float3(float.MinValue);

            var offsets = ChunkOffsets[_chunkIndex];
            var meshData = MeshDataArray[_chunkIndex];
            
            meshData.SetVertexBufferParams(offsets.VertexCount, VertexLayout);
            meshData.SetIndexBufferParams(offsets.TriangleCount, IndexFormat.UInt32);

            var vertices = meshData.GetVertexData<float3>(0);
            var uvs = meshData.GetVertexData<float3>(1);
            var colors = meshData.GetVertexData<float4>(2);
            var normals = meshData.GetVertexData<float3>(3);

            var triangles = meshData.GetIndexData<int>();

            _vertOffset = 0;
            _triOffset = 0;
            
            int blocksPerChunk = ChunkSize.GetTotalSize(BlockPadding);
            _chunkStartIndex = _chunkIndex * blocksPerChunk;
            
            _paddedWidth = ChunkSize.GetPaddedWidth(BlockPadding);
            
            // Process each of the 6 faces separately (greedy meshing works on 2D slices)
            // We'll do: Right(+X), Left(-X), Top(+Y), Bottom(-Y), Front(+Z), Back(-Z)
            GreedyMeshFace(new int3(1, 0, 0), vertices, normals, uvs, colors, triangles);  // Right face (+X)
            GreedyMeshFace(new int3(-1, 0, 0), vertices, normals, uvs, colors, triangles); // Left face (-X)

            GreedyMeshFace(new int3(0, 1, 0), vertices, normals, uvs, colors, triangles);  // Top face (+Y)
            GreedyMeshFace(new int3(0, -1, 0), vertices, normals, uvs, colors, triangles); // Bottom face (-Y)
            GreedyMeshFace(new int3(0, 0, 1), vertices, normals, uvs, colors, triangles);  // Front face (+Z)
            GreedyMeshFace(new int3(0, 0, -1), vertices, normals, uvs, colors, triangles); // Back face (-Z)

            offsets.BoundsMin = _boundsMin;
            offsets.BoundsMax = _boundsMax;

            ChunkOffsets[_chunkIndex] = offsets;

            meshData.subMeshCount = 1;
            meshData.SetSubMesh(0,
                new SubMeshDescriptor(
                    0,
                    offsets.TriangleCount,
                    MeshTopology.Triangles),
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }
        
        /// <summary>
        /// Performs greedy meshing for a single face direction
        /// </summary>
        /// <param name="normal">The normal vector of the face we're processing</param>
        private void GreedyMeshFace(
            int3 normal,
            NativeArray<float3> vertices,
            NativeArray<float3> normals,
            NativeArray<float3> uvs,
            NativeArray<float4> colors,
            NativeArray<int> triangles)
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
            
            // Mask array to track which blocks have been processed
            // One entry per block in the 2D slice we're examining
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
                for (int cv = 0; cv < maskDimV; cv ++)
                {
                    for (int cu = 0; cu < maskDimU; cu ++)
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
                        
                        Direction direction;
                        if (normal.x > 0) direction = Direction.Right;      // +X
                        else if (normal.x < 0) direction = Direction.Left;  // -X
                        else if (normal.y > 0) direction = Direction.Top;   // +Y
                        else if (normal.y < 0) direction = Direction.Bottom;// -Y
                        else if (normal.z > 0) direction = Direction.Front; // +Z
                        else direction = Direction.Back;                    // -Z
                        
                        // Create the merged quad
                        var textureId = BlockMap[maskEntry.BlockType].TextureData.GetTextureIndex(direction);
                        uint packedLight = maskEntry.PackedCornerLight;
                        int u = cu * _stepSize;
                        int v = cv * _stepSize;
                        CreateQuad(u, v, depth, width, height, normal, axisU, axisV, textureId, packedLight, vertices, normals, uvs, colors, triangles);
                        
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

        /// <summary>
        /// Creates a quad (two triangles) for a merged group of faces
        /// </summary>
        private void CreateQuad(
            int u,
            int v,
            int depth,
            int width,
            int height,
            int3 normal,
            int3 axisU,
            int3 axisV,
            int textureId, 
            uint packedLight,
            NativeArray<float3> vertices,
            NativeArray<float3> normals,
            NativeArray<float3> uvs,
            NativeArray<float4> colors,
            NativeArray<int> triangles)
        {
            // Get world position of bottom-left corner of the quad
            int3 basePos = GetPositionFromUVD(u, v, depth, normal, axisU, axisV);

            // Remove padding offset for actual mesh position
            float3 pos = new float3(basePos.x - BlockPadding, basePos.y, basePos.z - BlockPadding);

            // Calculate the four corners of the quad
            float3 v0 = pos;
            float3 v1 = pos + (float3)axisV * height * _stepSize;
            float3 v2 = pos + (float3)axisU * width * _stepSize
                            + (float3)axisV * height * _stepSize;
            float3 v3 = pos + (float3)axisU * width * _stepSize;

            // Offset vertices by normal direction (move face to block surface)
            if (normal.x > 0 || normal.y > 0 || normal.z > 0)
            {
                v0 += (float3)normal * _stepSize;
                v1 += (float3)normal * _stepSize;
                v2 += (float3)normal * _stepSize;
                v3 += (float3)normal * _stepSize;
            }
            
            _boundsMin = math.min(_boundsMin, v0);
            _boundsMin = math.min(_boundsMin, v1);
            _boundsMin = math.min(_boundsMin, v2);
            _boundsMin = math.min(_boundsMin, v3);

            _boundsMax = math.max(_boundsMax, v0);
            _boundsMax = math.max(_boundsMax, v1);
            _boundsMax = math.max(_boundsMax, v2);
            _boundsMax = math.max(_boundsMax, v3);

            // Get starting vertex index for this quad
            int startVert = _vertOffset;

            // Add vertices
            vertices[_vertOffset++] = v0;
            vertices[_vertOffset++] = v1;
            vertices[_vertOffset++] = v2;
            vertices[_vertOffset++] = v3;
            
            float3 faceNormal = (float3)normal;

            normals[startVert + 0] = faceNormal;
            normals[startVert + 1] = faceNormal;
            normals[startVert + 2] = faceNormal;
            normals[startVert + 3] = faceNormal;

            float l0 = (packedLight & 0xFF) / 15f;
            float l1 = ((packedLight >> 8) & 0xFF) / 15f;
            float l2 = ((packedLight >> 16) & 0xFF) / 15f;
            float l3 = ((packedLight >> 24) & 0xFF) / 15f;

            colors[startVert + 0] = new float4(l0, l0, l0, 1);
            colors[startVert + 1] = new float4(l1, l1, l1, 1);
            colors[startVert + 2] = new float4(l2, l2, l2, 1);
            colors[startVert + 3] = new float4(l3, l3, l3, 1);
            

        // Add triangles (two triangles form the quad)
            // Winding order depends on normal direction for correct backface culling
            bool flipWinding;
            if (normal.y != 0)
            {
                // Y-axis (top/bottom) uses original logic
                flipWinding = normal.y > 0;
            }
            else
            {
                // X and Z axes both need opposite logic
                flipWinding = normal.x < 0 || normal.z < 0;
            }
            
            if (!flipWinding)
            {
                triangles[_triOffset++] = startVert;
                triangles[_triOffset++] = startVert + 2;
                triangles[_triOffset++] = startVert + 1;
                
                triangles[_triOffset++] = startVert;
                triangles[_triOffset++] = startVert + 3;
                triangles[_triOffset++] = startVert + 2;
            }
            else
            {
                triangles[_triOffset++] = startVert;
                triangles[_triOffset++] = startVert + 1;
                triangles[_triOffset++] = startVert + 2;
                
                triangles[_triOffset++] = startVert;
                triangles[_triOffset++] = startVert + 2;
                triangles[_triOffset++] = startVert + 3;
            }
            
            // Add UVs - orientation depends on the face direction
            // For X-axis faces (Left/Right), we need to rotate the UVs
            if (normal.x != 0)
            {
                // X-axis faces need rotated UVs
                uvs[startVert] = new float3(0, 0, textureId);
                uvs[startVert + 1] = new float3(height, 0, textureId);
                uvs[startVert + 2] = new float3(height, width, textureId);
                uvs[startVert + 3] = new float3(0, width, textureId);
            }
            else if (normal.z != 0)
            {
                // Z-axis faces might need adjustment too
                uvs[startVert] = new float3(0, 0, textureId);
                uvs[startVert + 1] = new float3(0, height, textureId);
                uvs[startVert + 2] = new float3(width, height, textureId);
                uvs[startVert + 3] = new float3(width, 0, textureId);
            }
            else
            {
                // Y-axis faces (Top/Bottom)
                uvs[startVert] = new float3(0, 0, textureId);
                uvs[startVert + 1] = new float3(0, height, textureId);
                uvs[startVert + 2] = new float3(width, height, textureId);
                uvs[startVert + 3] = new float3(width, 0, textureId);
            }
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