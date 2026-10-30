namespace IPLMerchandise.Web.Client.Api;

/// <summary>A photo chosen in the admin form, held in memory until Save (at most 2 MB).</summary>
public sealed record ImageUpload(byte[] Content, string FileName, string ContentType)
{
    public string PreviewDataUrl => $"data:{ContentType};base64,{Convert.ToBase64String(Content)}";
}
