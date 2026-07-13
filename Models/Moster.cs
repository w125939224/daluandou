using System.ComponentModel.DataAnnotations;

namespace daluandou.Models
{
    public class Monster
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; }
        public int Attack { get; set; }
        public int Defense { get; set; }
        public int HP { get; set; }
        public int RewardGold { get; set; }
        public int? RewardEquipmentId { get; set; }
        public int? RewardMagicId { get; set; }
        public string? Description { get; set; }
    }
}