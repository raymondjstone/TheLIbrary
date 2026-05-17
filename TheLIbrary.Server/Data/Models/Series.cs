using System.ComponentModel.DataAnnotations;
namespace TheLibrary.Server.Data.Models;

public class Series
{
    public int Id { get; set; }
    [MaxLength(512)]
    public string Name { get; set; } = "";
    [MaxLength(512)]
    public string NormalizedName { get; set; } = "";
    public int? PrimaryAuthorId { get; set; }
    public Author? PrimaryAuthor { get; set; }
    public int? ParentSeriesId { get; set; }
    public Series? ParentSeries { get; set; }
    [MaxLength(20)]
    public string? PositionInParent { get; set; }
    public List<Series> ChildSeries { get; set; } = new();
    public List<SeriesAuthor> SeriesAuthors { get; set; } = new();
    public List<Book> Books { get; set; } = new();
}
