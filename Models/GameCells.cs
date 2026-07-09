using System.ComponentModel.DataAnnotations;

namespace daluandou.Models
{
    public class GameCells
    {
        [Key]
        public int Id { get; set; }
        public string? EventType { get; set; }
        public string? EventName { get; set; }
        public string? Description { get; set; }
        public int Value { get; set; }
        public int? MagicId { get; set; }
    }
}