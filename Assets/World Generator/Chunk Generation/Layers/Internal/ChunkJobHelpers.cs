using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

[BurstCompile]
public static class ChunkJobHelpers
{
    public static readonly int3[] Native_FaceChecks = new int3[6]
    {
        new (0, 0, -1),
        new (0, 0, 1),
        new (0, 1, 0),
        new (0, -1, 0),
        new (-1, 0, 0),
        new (1, 0, 0),
    };
    
    public static readonly int3[] Native_VoxelVertices = new int3[8]
    {
        new (0, 0, 0),
        new (1, 0, 0),
        new (1, 1, 0),
        new (0, 1, 0),
        new (0, 0, 1),
        new (1, 0, 1),
        new (1, 1, 1),
        new (0, 1, 1)
    };

    public static readonly int[] Native_VoxelTriangles = new int[24]
    {
        // Back face (dir 0)
        0, 3, 1, 2,
        // Front face (dir 1)
        5, 6, 4, 7,
        // Top face (dir 2)
        3, 7, 2, 6,
        // Bottom face (dir 3)
        1, 5, 0, 4,
        // Left face (dir 4)
        4, 7, 0, 3,
        // Right face (dir 5)
        1, 2, 5, 6
    };

    public static readonly int[] Native_VoxelVertexOrder = new int[6]
    {
        0, 1, 2, 2, 1, 3
    };

    public static readonly int2[] Native_VoxelUVs = new int2[4]
    {
        new (0, 0),
        new (0, 1),
        new (1, 0),
        new (1, 1),
    };
}
