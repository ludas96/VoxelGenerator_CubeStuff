using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class FpsCounter : MonoBehaviour
{
    [SerializeField] private TMP_Text _fpsText;
    [SerializeField] private float _hudRefreshRate = 0.05f;

    private float _timer;

    private void Update()
    {
        if (Time.unscaledTime > _timer)
        {
            int fps = (int)(1f / Time.unscaledDeltaTime);
            _fpsText.text = "FPS: " + fps;
            _fpsText.text += "\n---------------\n";
            _fpsText.text += World.Instance.GetStreamerStatistics();

        _timer = Time.unscaledTime + _hudRefreshRate;
        }
    }
}