namespace TopicMonitor.Processors;

/// <summary>
/// Plain C# model of one entry of the color classifier's YAML configuration (plan section 5):
/// <code>
/// color:
///   - input: led.*.raw
///     space: rgb
///     components: [r, g, b]
///     references:
///       off:    [0, 0, 0]
///       red:    [240, 20, 20]
///       green:  [20, 230, 30]
///       yellow: [230, 200, 20]
/// </code>
/// Loading the YAML into this shape is the Server's job; this type only accepts the already-parsed result.
/// </summary>
/// <param name="Input">Glob matched against raw topic names, e.g. "led.*.raw".</param>
/// <param name="Space">Color space identifier, e.g. "rgb". v1 only implements "rgb"/Euclidean distance over
/// <paramref name="Components"/>; the field is kept so future spaces (e.g. "hsv") can be added without
/// changing the config shape.</param>
/// <param name="Components">Component names in the order they appear in the raw vec value, e.g. ["r","g","b"].</param>
/// <param name="References">Reference color name -> component vector, e.g. "red" -> [240, 20, 20].</param>
/// <param name="K">Number of consecutive equal classifications required before a new color is accepted
/// (plan section 11, resolved: default k = 2).</param>
public sealed record ColorClassifierConfig(
    string Input,
    string Space,
    IReadOnlyList<string> Components,
    IReadOnlyDictionary<string, double[]> References,
    int K = 2);
