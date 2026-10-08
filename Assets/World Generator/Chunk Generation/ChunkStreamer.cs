using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using World_Generator.Chunk_Generation.Layers;

namespace World_Generator.Chunk_Generation
{

    public struct ChunkHandle
    {
        public Vector3Int Position;
        public uint Version;
        
        public ChunkHandle(Vector3Int position, uint version)
        {
            Position = position;
            Version = version;
        }
    }
    
    public interface IChunkWorldState
    {
        bool TryGet(ChunkHandle handle, out ChunkWorldState.ChunkEntry entry);
        bool IsCurrent(ChunkHandle handle);
    }
    public sealed class ChunkWorldState : IChunkWorldState
    {
        // TODO: Ändra states..?
        public enum ChunkState
        {
            Pending,
            Generated,
            Meshing,
            Meshed
        }
        
        public sealed class ChunkEntry
        {
            public ChunkHandle Handle;
            public Chunk Chunk;
            public ChunkState State;
        }

        private readonly Dictionary<Vector3Int, uint> _chunkVersions = new();
        private readonly Dictionary<Vector3Int, ChunkEntry> _chunks = new();

        public bool TryGet(ChunkHandle handle, out ChunkEntry entry)
        {
            entry = default;
            
            if (_chunks.TryGetValue(handle.Position, out entry))
                return true;
            
            return false;
        }

        public bool IsCurrent(ChunkHandle handle) => _chunks.TryGetValue(handle.Position, out var current)
                                                     && current.Handle.Version == handle.Version;

        internal ChunkHandle AddOrUpdate(Vector3Int pos, Chunk chunk)
        {
            var handle = new ChunkHandle(pos, GetNextVersion(pos));
            if (_chunks.TryGetValue(pos, out _))
            {
                _chunks[pos] = new ChunkEntry()
                {
                    Handle = handle,
                    Chunk = chunk,
                    State = ChunkState.Pending
                };
                return handle;
            }

            _chunks.Add(pos, new ChunkEntry()
            {
                Handle = handle,
                Chunk = chunk,
                State = ChunkState.Pending
            });

            return handle;
        }

        internal void Remove(Vector3Int pos)
        {
            _chunks.Remove(pos);
        }

        internal void SetState(ChunkHandle handle, ChunkState state)
        {
            if (IsCurrent(handle))
                _chunks[handle.Position].State = state;
        }

        internal ChunkData SetChunkData(ChunkHandle handle, ChunkData data)
        {
            if (!_chunks.TryGetValue(handle.Position, out var entry)) return null;
            
            entry.Chunk.SetChunkData(data.Blocks, data.LightLevels);
            return entry.Chunk.GetChunkData();
        }

        private uint GetNextVersion(Vector3Int pos)
        {
            if(!_chunkVersions.TryGetValue(pos, out _)) _chunkVersions.Add(pos, 0);
            
            _chunkVersions[pos] ++;
            return _chunkVersions[pos];
        }

        internal void Dispose()
        {
            _chunks.Clear();
            _chunkVersions.Clear();
        }
        
        internal int CountChunksInState(ChunkState state)
        {
            return _chunks.Values.Count(x => x.State == state);
        }
    }
    
    public sealed class ChunkStreamer
    {
        public struct ChunkStreamerSettings
        {
            public int ViewDistance { get; set; } // 25
            public Transform CameraTransform { get; set; }
            public ChunkSize ChunkSize { get; set; } // 16x16x256
            public GameObject Container { get; set; }
        }
        
        private ChunkLoader _loader;
        private ChunkGenerator _generator;
        private ChunkMesher _mesher;

        private readonly ChunkWorldState _worldState = new();

        private ChunkStreamerSettings _settings;
        
        public ChunkStreamer(ChunkStreamerSettings settings)
        {
            _settings = settings;
            
            _loader = new ChunkLoader(new ChunkLoaderSettings
            {
                ChunkLoadBudgetPerFrame = 25,
                ChunkViewDistance = settings.ViewDistance,
                PlayerTransform = settings.CameraTransform,
                ChunkSize = settings.ChunkSize,
                Container = settings.Container
            });
            
            _generator = new ChunkGenerator(new ChunkGeneratorSettings
            {
                ChunkSize = settings.ChunkSize,
                TasksPerThread = 5,
                MaxActiveBatches = 10,
                MaxChunksPerBatch = 5
            }).WithLayers(
                new BaseTerrainLayer(),
                new ApplyModificationsLayer(),
                new LightLayer()
            );
            
            _mesher = new ChunkMesher(new ChunkMesher.ChunkMesherSettings
            {
                ChunkSize =  settings.ChunkSize,
                TasksPerThread = 5,
                MaxActiveBatches = 10,
                MaxChunksPerBatch = 5
            });
        }

        public void Update()
        {
            /* ChunkLoader */
            _loader.Update();
            HandleChunkLoader();
            
            /* ChunkGenerator */
            _generator.Update();
            HandleChunkGenerator();
            
            
            /* ChunkMesher */
            _mesher.Update();
            HandleChunkMesher();
            
        }

        private void HandleChunkLoader()
        {
            while (_loader.TryGetUnloadRequest(out var request))
            {
                _worldState.Remove(request);
            }
            while (_loader.TryGetLoadRequest(out var request))
            {
                var handle = _worldState.AddOrUpdate(request.Position, request.Chunk);
                if(request.LoadType == ChunkLoader.LoadType.New)
                    _generator.Add(new ChunkGenerator.GenerationRequest(){ Handle = handle, Edits = request.Edits});
                
                else if (request.LoadType == ChunkLoader.LoadType.Update)
                {
                    if (_worldState.TryGet(handle, out var entry))
                    {
                        var chunkData = entry.Chunk.GetChunkData();
                        
                        var pos = _settings.CameraTransform.position;
                        var playerChunkPos = ChunkHelpers.GetChunkPosFromWorldPos(pos, _settings.ChunkSize);

                        var lod = ChunkHelpers.GetLODAtDistance(Vector3Int.Distance(playerChunkPos, request.Position));
                        _mesher.Add(new MesherRequest(){ Handle = handle, Data = chunkData, LOD = lod });
                    }
                }
            }
        }
        private void HandleChunkGenerator()
        {
            while (_generator.TryGetCompleted(out var result))
            {
                if (!_worldState.IsCurrent(result.Handle))
                    continue;
                
                _worldState.SetState(result.Handle, ChunkWorldState.ChunkState.Generated);
                var chunkData = _worldState.SetChunkData(result.Handle, result.Data); // Copy ChunkData to the Chunk for persistent storage
                
                // Fetch LOD for the given chunk
                
                var pos = _settings.CameraTransform.position;
                var playerChunkPos = ChunkHelpers.GetChunkPosFromWorldPos(pos, _settings.ChunkSize);

                var lod = ChunkHelpers.GetLODAtDistance(Vector3Int.Distance(playerChunkPos, result.Handle.Position));
                _mesher.Add(new MesherRequest(){ Handle = result.Handle, Data = chunkData, LOD = lod });
            }
        }

        private void HandleChunkMesher()
        {
            while (_mesher.TryGetReadyBatch(out var batch))
            {
                var chunks = new Chunk[batch.TotalChunks];
                for (int i = 0; i < batch.TotalChunks; i++)
                {
                    var handle = batch.ChunkHandles[i];
                    
                    if (!_worldState.IsCurrent(handle)) continue;
                    
                    _worldState.SetState(handle, ChunkWorldState.ChunkState.Meshing);

                    if (_worldState.TryGet(handle, out var entry))
                        chunks[i] = entry.Chunk;
                }
                
                _mesher.ApplyMeshBatch(batch, chunks);
            }
            while (_mesher.TryGetMeshed(out var result))
            {
                if (!_worldState.IsCurrent(result.Handle))
                    continue;
                
                _worldState.SetState(result.Handle, ChunkWorldState.ChunkState.Meshed);
            }
        }

        public void Dispose()
        {
            _loader.Dispose();
            _generator.Dispose();
            _mesher.Dispose();

            _worldState.Dispose();
        }
        
        public void SetBlock(Vector3 worldPos, BlockTypes block)
        {
            _loader.SetBlock(worldPos, block);
        }

        public BlockTypes? GetBlockAt(Vector3 worldPos)
        {
            return _loader.GetBlock(worldPos);
        }

        public string GetStateStatistics()
        {
            var pendingCount = _worldState.CountChunksInState(ChunkWorldState.ChunkState.Pending);
            var generatedCount = _worldState.CountChunksInState(ChunkWorldState.ChunkState.Generated);
            var meshingCount = _worldState.CountChunksInState(ChunkWorldState.ChunkState.Meshing);
            var meshedCount = _worldState.CountChunksInState(ChunkWorldState.ChunkState.Meshed);

            return $"Pending: {pendingCount}\n" +
                   $"Generated: {generatedCount}\n" +
                   $"Meshing: {meshingCount}\n" +
                   $"Meshed: {meshedCount}\n";
        }
    }
}