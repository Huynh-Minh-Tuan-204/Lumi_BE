namespace Lumi.Domain.Entities
{
    public class Department
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int? CreatedBy { get; set; }
        public User CreatedByUser { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}