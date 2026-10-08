using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Unity.Burst;
using Unity.Profiling;
using UnityEngine;
using World_Generator.Chunk_Generation;
using World_Generator.Chunk_Generation.Layers;


public struct ChunkLoaderSettings
{
    public int ChunkViewDistance;
    public int ChunkLoadBudgetPerFrame;
    
    public Transform PlayerTransform;
    public GameObject Container;

    public ChunkSize ChunkSize;
}

// Handles what chunks to load/unload
public class ChunkLoader
{
    public enum LoadType
    {
        New,
        Update,
    }
    
    public sealed class ChunkLoadRequest
    {
        public Chunk Chunk;
        public Vector3Int Position;
        [CanBeNull] public List<ChunkEdit> Edits = null;

        public LoadType LoadType = LoadType.New;
    }
    
    private int _chunkViewDistance = 100;
    private int _chunkLoadBudgetPerFrame = 5;   
    
    private ChunkSize _chunkSize;
    
    private Transform _playerTransform;
    private Transform _containerTransform;

    private Dictionary<Vector3Int, Chunk> _loadedChunks = new();
    private Dictionary<Vector3Int, List<ChunkEdit>> _editedChunks = new();

    private Queue<Vector3Int> _pendingCreateQueue = new();
    private Queue<Vector3Int> _pendingRemoveQueue = new();

    private Queue<Vector3Int> _removeQueue = new();
    private Queue<ChunkLoadRequest> _loadQueue = new();

    public Vector3Int LastFetchedPosition;
    private bool _initialized;
    
    private ChunkPool _chunkPool;

    public ChunkLoader(ChunkLoaderSettings settings)
    {
        _chunkViewDistance = settings.ChunkViewDistance;
        _chunkLoadBudgetPerFrame = settings.ChunkLoadBudgetPerFrame;
        _playerTransform = settings.PlayerTransform;
        _chunkSize = settings.ChunkSize;
        _containerTransform = settings.Container.transform;
        
        _chunkPool = new ChunkPool(_chunkViewDistance * _chunkViewDistance, _containerTransform);
        _chunkPool.Initialize();
    }
    
    public void Update()
    {
        Vector3Int centerChunk = GetPlayerChunk();
        
        if(!_initialized || centerChunk != LastFetchedPosition)
        {
            UpdateChunks(centerChunk);
            
            _initialized = true;
        }

        ProcessRemoveQueue();
        ProcessCreateQueue();
    }

    private void UpdateChunks(Vector3Int newCenter)
    {
        if (!_initialized)
        {
            LastFetchedPosition = newCenter;
            FullInitialize();
            return;
        }

        Vector3Int delta = newCenter - LastFetchedPosition;
        if (delta == Vector3Int.zero)
            return;

        ProcessDelta(LastFetchedPosition, newCenter);
        LastFetchedPosition = newCenter;
    }

    private void FullInitialize()
    {
        int half = _chunkViewDistance / 2;

        // Ring expansion: nearest → farthest
        for (int r = 0; r <= half; r++)
        {
            for (int z = -r; z <= r; z++)
            for (int x = -r; x <= r; x++)
            {
                // Only take border cells of this ring
                if (Mathf.Abs(x) != r && Mathf.Abs(z) != r)
                    continue;

                var pos = new Vector3Int(LastFetchedPosition.x + x, 0, LastFetchedPosition.z + z);
                EnqueueCreate(pos);
            }
        }
    }

    private void ProcessDelta(Vector3Int oldCenter, Vector3Int newCenter)
    {
        int half = _chunkViewDistance / 2;

        int dx = newCenter.x - oldCenter.x;
        int dz = newCenter.z - oldCenter.z;
        
        // X (Row)
        if (dx != 0)
        {
            int dir = Math.Sign(dx);

            for (int step = 0; step < Math.Abs(dx); step++)
            {
                int removeX = oldCenter.x - dir * half + step * dir;
                int addX = newCenter.x + dir * half - step * dir;

                for (int z = -half; z <= half; z++)
                {
                    EnqueueRemove(new Vector3Int(removeX, 0, newCenter.z + z));
                    EnqueueCreate(new Vector3Int(addX, 0, newCenter.z + z));
                }
            }
        }
        
        // Z (Column)
        if (dz != 0)
        {
            int dir = Math.Sign(dz);

            for (int step = 0; step < Math.Abs(dz); step++)
            {
                int removeZ = oldCenter.z - dir * half + step * dir;
                int addZ = newCenter.z + dir * half - step * dir;

                for (int x = -half; x <= half; x++)
                {
                    EnqueueRemove(new Vector3Int(newCenter.x + x, 0, removeZ));
                    EnqueueCreate(new Vector3Int(newCenter.x + x, 0, addZ));
                }
            }
        }
        
        // Existing chunks inside the new view distance.
        // Re-sample their LOD.
        int minX = Math.Max(oldCenter.x - half, newCenter.x - half);
        int maxX = Math.Min(oldCenter.x + half, newCenter.x + half);

        int minZ = Math.Max(oldCenter.z - half, newCenter.z - half);
        int maxZ = Math.Min(oldCenter.z + half, newCenter.z + half);

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                EnqueueCreate(new Vector3Int(x, 0, z));
            }
        }
    }

    private void EnqueueCreate(Vector3Int pos)
    {
        if(!_loadedChunks.ContainsKey(pos))
            _pendingCreateQueue.Enqueue(pos);
        else
        {
            var lod = ChunkHelpers.GetLODAtDistance(Vector3Int.Distance(GetPlayerChunk(), pos));
            if(_loadedChunks[pos].LOD != lod)
                _pendingCreateQueue.Enqueue(pos);
        }
    }
    
    private void EnqueueRemove(Vector3Int pos)
    {
        if(_loadedChunks.ContainsKey(pos))
            _pendingRemoveQueue.Enqueue(pos);
    }

    private Vector3Int GetPlayerChunk()
    {
        var pos = _playerTransform.position;
        return ChunkHelpers.GetChunkPosFromWorldPos(pos, _chunkSize);
    }

    private void ProcessRemoveQueue()
    {
        int budget = _chunkLoadBudgetPerFrame;

        while (budget-- > 0 && _pendingRemoveQueue.Count > 0)
        {
            var pos = _pendingRemoveQueue.Dequeue();

            if (_loadedChunks.TryGetValue(pos, out var chunk))
            {
                chunk.Reset();
                _chunkPool.Return(chunk);
                _loadedChunks.Remove(pos);
                
                _removeQueue.Enqueue(pos);
            }
        }
    }

    private void ProcessCreateQueue()
    {
        int budget = _chunkLoadBudgetPerFrame;
        
        while (budget-- > 0 && _pendingCreateQueue.Count > 0)
        {
            var pos = _pendingCreateQueue.Dequeue();
            if (!_loadedChunks.ContainsKey(pos))
            {
                var chunk = _chunkPool.Get();
                // TODO: Se över om initialize behövs?
                chunk.Initialize(pos, _containerTransform);
                
                _loadedChunks.Add(pos, chunk);
                _loadQueue.Enqueue(new ChunkLoadRequest()
                {
                    Chunk = chunk,
                    Position = pos,
                    Edits = _editedChunks.GetValueOrDefault(pos)
                });
            }
            else
            {
                var chunk = _loadedChunks[pos];
                var lod = ChunkHelpers.GetLODAtDistance(Vector3Int.Distance(GetPlayerChunk(), pos));
                if (chunk.LOD == lod) continue;
                
                _loadQueue.Enqueue(new ChunkLoadRequest()
                {
                    Chunk = chunk,
                    Position = pos,
                    Edits = _editedChunks.GetValueOrDefault(pos),
                    LoadType = LoadType.Update
                });
            }
        }
    }
    
    public void SetBlock(Vector3 pos, BlockTypes block)
    {
        var chunkPos = ChunkHelpers.GetChunkPosFromWorldPos(pos, _chunkSize);
        if (!_loadedChunks.TryGetValue(chunkPos, out var chunk)) // No chunk at current pos, should never happen..
            return;
        
        var blockPos = ChunkHelpers.GetBlockPosFromWorldPos(pos, _chunkSize);
        if (chunk.GetBlock(blockPos) == block) return; // Same as current block
        
        AddNewChunkEdit(chunkPos,
            new Vector3Int(
                blockPos.x + GeneratorConstants.BLOCK_PADDING,
                blockPos.y,
                blockPos.z + GeneratorConstants.BLOCK_PADDING)
            , block);
        
        var editedNeighbors = ChunkHelpers.GetNeighborChunksBlock(blockPos, chunkPos, _chunkSize);
        foreach (var neighbor in editedNeighbors)
        {
            AddNewChunkEdit(neighbor.chunk, neighbor.block, block);
        }
    }

    public BlockTypes? GetBlock(Vector3 worldPos)
    {
        var chunkPos = ChunkHelpers.GetChunkPosFromWorldPos(worldPos, _chunkSize);
        if (!_loadedChunks.TryGetValue(chunkPos, out var chunk))
            return null;
        
        var blockPos =  ChunkHelpers.GetBlockPosFromWorldPos(worldPos, _chunkSize);
        var block = chunk.GetBlock(blockPos);
        
        if (block == BlockTypes.Air) return null;
        
        return block;
    }

    private void AddNewChunkEdit(Vector3Int pos, Vector3Int blockPos, BlockTypes block)
    {
        _editedChunks.AddOrInsertChunkEdit(pos, 
            new ChunkEdit()
            {
                Block = block,
                LocalIndex = ChunkHelpers.GetIndexFromBlockPos(blockPos, _chunkSize)
            }
        );
        
        _loadQueue.Enqueue(new ChunkLoadRequest()
        {
            Chunk = _loadedChunks[pos],
            Position = pos,
            Edits = _editedChunks.GetValueOrDefault(pos)
        });
    }

    public bool TryGetUnloadRequest(out Vector3Int chunkPos)
    {
        chunkPos = default;
        if (_removeQueue.Count == 0) return false;

        chunkPos = _removeQueue.Dequeue();
        return true;
    }

    public bool TryGetLoadRequest(out ChunkLoadRequest request)
    {
        request = default;
        if (_loadQueue.Count == 0) return false;

        request = _loadQueue.Dequeue();
        return true;
    }
    
    public void Dispose()
    {
        _chunkPool.Dispose();

        foreach (var chunk in _loadedChunks.Values)
        {
            chunk.Dispose();
        }
        
        _loadedChunks.Clear();
        _loadedChunks = null;
        _pendingRemoveQueue = null;
        _pendingCreateQueue = null;
        _loadQueue.Clear();
        _removeQueue.Clear();
    }
}

public class ChunkPool
{
    private readonly Stack<Chunk> _pool;
    private int _size;

    private Transform _containerTransform;
    
    public ChunkPool(int size, Transform container)
    {
        _pool = new Stack<Chunk>(size);
        _size = size;
        
        _containerTransform = container;
    }

    public void Initialize()
    {
        for (int i = 0; i < _size; i++)
        {
            _pool.Push(new Chunk(_containerTransform));
        }
    }

    public Chunk Get()
    {
        if (_pool.Count > 0)
            return _pool.Pop();

        return new Chunk(_containerTransform);
    }

    public void Return(Chunk chunk)
    {
        _pool.Push(chunk);
    }

    public void Dispose()
    {
        foreach (var chunk in _pool)
            chunk.Dispose();
        
        _pool.Clear();
    }
}

internal static class ChunkEditExtensions {
    public static void AddOrInsertChunkEdit(this Dictionary<Vector3Int, List<ChunkEdit>> dictionary, Vector3Int chunkPos, ChunkEdit newEdit)
    {
        if (!dictionary.ContainsKey(chunkPos))
            dictionary.Add(chunkPos, new List<ChunkEdit>() { newEdit });
        else
            dictionary[chunkPos].AddOrReplaceChunkEdit(newEdit);
    }

    private static void AddOrReplaceChunkEdit(this List<ChunkEdit> list, ChunkEdit value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (item.LocalIndex != value.LocalIndex) continue;
            
            if (item.Block == value.Block) return;
            list[i] = value;
            return;
        }
        
        list.Add(value);
    }
}
