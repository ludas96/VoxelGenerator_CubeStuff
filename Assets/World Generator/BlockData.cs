using System;
using Unity.Collections;
using UnityEngine;

[System.Serializable]
public enum BlockTypes : byte
{
    Air,
    Dirt,
    Dirt_Grass,
    Grass,
    Stone,
    Stone_Grass,
    Glass
}

[System.Serializable]
public struct BlockData
{
    public FixedString64Bytes BlockName;
    public bool IsSolid;
    public BlockTextureData TextureData;
}

[System.Serializable]
public struct BlockTextureData
{
    [Header("Texture Array Indices")]
    public int Top;
    public int Bottom;
    public int Left;
    public int Right;
    public int Back;
    public int Front;

    public int GetTextureIndex(Direction direction) => direction switch
    {
        Direction.Back => Back,
        Direction.Front => Front,
        Direction.Top => Top,
        Direction.Bottom => Bottom,
        Direction.Left => Left,
        Direction.Right => Right,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
    };
}

[System.Serializable]
public struct BlockTypeEntry
{
    public BlockTypes Identifier;
    public BlockData Data;

    public BlockTypeEntry(BlockTypes identifier, BlockData data)
    {
        if (string.IsNullOrEmpty(data.BlockName.Value))
        {
            data.BlockName =  identifier.ToString();
        }
        
        Identifier = identifier;
        Data = data;
    }

    public override string ToString()
    {
        return $"{Identifier} (\"{Data.BlockName}\" - Solid: {(Data.IsSolid ? "Yes" : "No")})";
    }
}

public enum Direction
{
    Back,
    Front,
    Top,
    Bottom,
    Left,
    Right
}