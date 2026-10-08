using System;
using UnityEngine;
using UnityEngine.InputSystem.LowLevel;
using World_Generator.Chunk_Generation;

public class BlockVisualizer : MonoBehaviour
{
    public GameObject SelectedBlock;
    public GameObject PlacementBlock;
    
    public Transform CameraTransform;

    [Range(1, 100)]
    public int BlockRange = 5;

    private int _terrainLayer;

    private void Awake()
    {
        _terrainLayer = 1 << LayerMask.NameToLayer("Terrain");        
    }

    private void LateUpdate()
    {
        if (Physics.Raycast(CameraTransform.position, CameraTransform.forward, out RaycastHit hit, BlockRange,  _terrainLayer))
        {
            var hitBlock = hit.point - hit.normal * 0.5f;
            hitBlock += hit.normal * 0.001f;
            if (World.Instance.GetBlockAt(hitBlock) != null)
            {
                Vector3Int blockCell = Vector3Int.FloorToInt(hitBlock);
                SelectedBlock.transform.position = blockCell + Vector3.one * 0.5f;
                PlacementBlock.transform.position = blockCell + Vector3.one * 0.5f + hit.normal;
                
                SelectedBlock.SetActive(true);
                PlacementBlock.SetActive(true);
            }
            else
            {
                SelectedBlock.SetActive(false);
                PlacementBlock.SetActive(false);
            }
        }
        else
        {
            SelectedBlock.SetActive(false);
            PlacementBlock.SetActive(false);
        }

        if (!SelectedBlock.activeSelf) return;
        
        if (Input.GetMouseButtonDown((int)MouseButton.Right))
        {
            World.Instance.SetBlock(PlacementBlock.transform.position, BlockTypes.Stone);
        }
        else if (Input.GetMouseButtonDown((int)MouseButton.Left))
        {
            World.Instance.SetBlock(SelectedBlock.transform.position, BlockTypes.Air);
        }
    }
}
