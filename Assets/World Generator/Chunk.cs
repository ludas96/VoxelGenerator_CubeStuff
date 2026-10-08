using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using World_Generator.Chunk_Generation;
using Object = UnityEngine.Object;

public class Chunk
{
    private GameObject _chunkObject;

    public Mesh Mesh;
    
    private MeshRenderer _meshRenderer;
    private MeshFilter _meshFilter;
    private MeshCollider _meshCollider;

    private ChunkData _chunkData;

    private Vector3Int _localPosition;
    private Vector3Int _worldPosition;

    private bool _initialized;
    private bool _isMeshed;
    public bool IsDone => _initialized && _isMeshed;
    public Vector3Int ChunkPosition => _localPosition;

    public int LOD;

    public Chunk(Transform container)
    {
        _chunkObject = new GameObject();
        _chunkObject.transform.SetParent(container);
        _chunkData = new ChunkData();
        
        Mesh = new Mesh();
        
        _meshRenderer = _chunkObject.AddComponent<MeshRenderer>();
        _meshFilter = _chunkObject.AddComponent<MeshFilter>();
        _meshCollider = _chunkObject.AddComponent<MeshCollider>();
        
        _chunkData.Blocks = new NativeArray<byte>(
            World.Instance.ChunkSize.GetTotalSize(GeneratorConstants.BLOCK_PADDING),
            Allocator.Persistent
        );
        _chunkData.LightLevels = new NativeArray<byte>(
            World.Instance.ChunkSize.GetTotalSize(GeneratorConstants.BLOCK_PADDING),
            Allocator.Persistent
        );
    }
    
    public void Initialize(Vector3Int pos, Transform parent = null)
    {
        _worldPosition = new Vector3Int(
            pos.x * World.Instance.ChunkSize.Width, 
            pos.y * World.Instance.ChunkSize.Height,
            pos.z * World.Instance.ChunkSize.Width
        );
        
        _localPosition = pos;

        if (!_initialized)
        {
            _chunkObject.transform.position = _worldPosition;
            _meshRenderer.sharedMaterial = World.Instance.VoxelMaterial;
            _chunkObject.layer = LayerMask.NameToLayer("Terrain");
        }
        
        _initialized = true;
    }

    public void SetChunkData(NativeArray<byte> blocks, NativeArray<byte> lightLevels)
    {
        blocks.CopyTo(_chunkData.Blocks);
        lightLevels.CopyTo(_chunkData.LightLevels);
        _chunkData.MarkChanged();
    }

    public ChunkData GetChunkData()
    {
        return _chunkData;
    }
    
    public void SetBlock(NativeArray<byte> blocks)
    {
        blocks.CopyTo(_chunkData.Blocks);
    }

    public BlockTypes GetBlock(Vector3Int blockPos)
    {
        // offset by padding
        blockPos = new Vector3Int(blockPos.x + GeneratorConstants.BLOCK_PADDING, blockPos.y, blockPos.z + GeneratorConstants.BLOCK_PADDING);
        
        var index = ChunkHelpers.GetIndexFromBlockPos(blockPos, World.Instance.ChunkSize);
        return (BlockTypes)_chunkData.Blocks[index];
    }

    public void Setup(int lod = 1)
    {
        _chunkObject.SetActive(true);
        
        _chunkObject.transform.position = _worldPosition;
        _chunkObject.name = $"Chunk ({_localPosition})";
        _meshFilter.sharedMesh = Mesh;
        if (Mesh.vertexCount > 0)
        {
            _meshCollider.sharedMesh = Mesh;
        }

        _isMeshed = true;
        LOD = lod;
    }
    

    public void Reset()
    {
        if (_chunkObject)
        {
            _chunkObject.SetActive(false);
        }
        
        _meshFilter.mesh?.Clear();
        Mesh.Clear();

        _isMeshed = false;
    }
    
    public void Dispose()
    {
        _meshFilter.mesh?.Clear();
        _chunkData.Dispose();
        
        Object.Destroy(_chunkObject);
    }
}

[System.Serializable]
[BurstCompile]
public struct ChunkSize
{
    public int Width;
    public int Height;

    public ChunkSize(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int GetTotalSize(int padding = 0)
    {
        return GetPaddedWidth(padding) * Height * GetPaddedWidth(padding);
    }

    public int GetPaddedWidth(int padding = 0)
    {
        return (Width + padding * 2);
    }
}

[BurstCompile]
public struct ChunkEdit : IEquatable<ChunkEdit>
{
    public int LocalIndex;
    public BlockTypes Block;

    public bool Equals(ChunkEdit other)
    {
        return LocalIndex == other.LocalIndex;
    }

    public override bool Equals(object obj)
    {
        return obj is ChunkEdit other && Equals(other);
    }

    public override int GetHashCode()
    {
        return LocalIndex.GetHashCode();
    }
}
