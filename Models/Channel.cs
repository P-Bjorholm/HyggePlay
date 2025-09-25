using System;

namespace HyggePlay.Models
{
    public class Channel
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
        public string Logo { get; set; } = string.Empty;
        public string TvgId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public override string ToString() => Name;
    }
}