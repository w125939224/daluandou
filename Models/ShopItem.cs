using System.ComponentModel.DataAnnotations;

namespace daluandou.Models
{
    public class ShopItem
    {
        [Key]
        public int Id { get; set; }
        public string ItemType { get; set; }
        public int? ItemId { get; set; }
        public string ItemName { get; set; }
        public int Price { get; set; }
        public string? Description { get; set; }
    }
}