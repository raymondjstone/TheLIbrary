namespace TheLibrary.Server.Data.Models;

public class NzbSite
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string UrlTemplate { get; set; } = "";
    public int Order { get; set; } = 99;
    public bool Active { get; set; } = true;
}
