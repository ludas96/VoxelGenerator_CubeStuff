using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Unity.Profiling;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering;
using World_Generator.Chunk_Generation;
using World_Generator.Chunk_Generation.Layers;
using MouseButton = UnityEngine.InputSystem.LowLevel.MouseButton;

public class World : MonoBehaviour
{
    public static World Instance;
    public FastNoiseLiteInstance Noise;
    
    private ChunkStreamer _chunkStreamer;
    
    public ChunkSize ChunkSize = new ChunkSize(16, 256);
    public int ChunkViewDistance = 25;
    
    public Material VoxelMaterial;
    public Texture2DArray TextureArray;
    
    public Transform CameraTransform;
    
    [SerializeField] private BlockTypeEntry[] _BlockTypes;
    public readonly Dictionary<BlockTypes, BlockData> BlockDataMap = new ();

    
    private void Awake()
    {
        if (Instance) return;

        Instance = this;

        Debug.Log($"Adding block types...");
        int blockTypeCount = 0;
        foreach (var block in _BlockTypes)
        {
            BlockDataMap.Add(block.Identifier, block.Data);
            Debug.Log($"Added block type #{blockTypeCount} - {block}");
            blockTypeCount++;
        }
        
        VoxelMaterial.SetTexture("_VoxelTextureArray", TextureArray);

        _chunkStreamer = new ChunkStreamer(new ChunkStreamer.ChunkStreamerSettings
        {
            ChunkSize = ChunkSize,
            Container = gameObject,
            ViewDistance = ChunkViewDistance,
            CameraTransform =  CameraTransform,
        });
    }

    private void Update()
    {
        _chunkStreamer.Update();
    }

    public void SetBlock(Vector3 worldPos, BlockTypes block)
    {
        _chunkStreamer.SetBlock(worldPos, block);
    }

    public BlockTypes? GetBlockAt(Vector3 worldPos)
    {
        return _chunkStreamer.GetBlockAt(worldPos);
    }

    private void OnDestroy()
    {
        _chunkStreamer.Dispose();
    }

    public string GetStreamerStatistics()
    {
        return _chunkStreamer.GetStateStatistics();
    }
}
