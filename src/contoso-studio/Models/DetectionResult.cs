namespace VideoStudio.Models;

public class DetectionResult
{
    public int FrameIndex { get; set; }
    public string Label { get; set; } = "";
    public float Confidence { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}
