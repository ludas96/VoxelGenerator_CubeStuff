#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

/// <summary>
/// Converts a texture atlas into a Texture2DArray for voxel rendering
/// Place this in an Editor folder
/// </summary>
public class TextureAtlasToArrayConverter : EditorWindow
{
    [SerializeField] private Texture2D atlasTexture;
    [SerializeField] private int tilesPerRow = 4;
    [SerializeField] private int tilesPerColumn = 4;
    [SerializeField] private string outputPath = "Assets/VoxelTextureArray.asset";
    
    [MenuItem("Tools/Texture Atlas to Array Converter")]
    public static void ShowWindow()
    {
        GetWindow<TextureAtlasToArrayConverter>("Atlas to Array");
    }
    
    private void OnGUI()
    {
        GUILayout.Label("Texture Atlas to Texture2DArray", EditorStyles.boldLabel);
        
        atlasTexture = (Texture2D)EditorGUILayout.ObjectField(
            "Atlas Texture", atlasTexture, typeof(Texture2D), false);
        
        tilesPerRow = EditorGUILayout.IntField("Tiles Per Row", tilesPerRow);
        tilesPerColumn = EditorGUILayout.IntField("Tiles Per Column", tilesPerColumn);
        outputPath = EditorGUILayout.TextField("Output Path", outputPath);
        
        if (GUILayout.Button("Convert to Texture2DArray"))
        {
            ConvertAtlasToArray();
        }
    }
    
    private void ConvertAtlasToArray()
    {
        if (atlasTexture == null)
        {
            EditorUtility.DisplayDialog("Error", "Please assign an atlas texture", "OK");
            return;
        }
        
        // Make texture readable
        string path = AssetDatabase.GetAssetPath(atlasTexture);
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null && !importer.isReadable)
        {
            importer.isReadable = true;
            AssetDatabase.ImportAsset(path);
        }
        
        int tileWidth = atlasTexture.width / tilesPerRow;
        int tileHeight = atlasTexture.height / tilesPerColumn;
        int totalTiles = tilesPerRow * tilesPerColumn;
        
        // Create Texture2DArray
        Texture2DArray textureArray = new Texture2DArray(
            tileWidth, 
            tileHeight, 
            totalTiles,
            TextureFormat.RGBA32, 
            false, 
            false
        );
        
        textureArray.filterMode = FilterMode.Point; // For pixel art voxels
        textureArray.wrapMode = TextureWrapMode.Clamp;
        
        // Extract each tile from atlas
        for (int y = 0; y < tilesPerColumn; y++)
        {
            for (int x = 0; x < tilesPerRow; x++)
            {
                int index = y * tilesPerRow + x;
                
                // Get pixels from atlas (flip Y because Unity's texture coordinates)
                Color[] pixels = atlasTexture.GetPixels(
                    x * tileWidth,
                    (tilesPerColumn - 1 - y) * tileHeight,
                    tileWidth,
                    tileHeight
                );
                
                textureArray.SetPixels(pixels, index);
            }
        }
        
        textureArray.Apply(true);
        
        // Save as asset
        AssetDatabase.CreateAsset(textureArray, outputPath);
        AssetDatabase.SaveAssets();
        
        Debug.Log($"Created Texture2DArray with {totalTiles} textures at {outputPath}");
        EditorUtility.DisplayDialog("Success", 
            $"Created Texture2DArray with {totalTiles} textures", "OK");
    }
}
#endif