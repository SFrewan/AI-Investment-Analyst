namespace AI_Investment_Domain.Entity
{
    public class Company
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Ticker { get; set; } = string.Empty;

        public string? Exchange { get; set; }

        public string? Sector { get; set; }

        public string? Industry { get; set; }

        public string? Country { get; set; }

        public string? Description { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
