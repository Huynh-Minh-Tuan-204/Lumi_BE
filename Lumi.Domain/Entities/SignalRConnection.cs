namespace Lumi.Domain.Entities
{
    public class SignalRConnection
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public User User { get; set; }
        public int DeviceId { get; set; }
        public UserDevice Device { get; set; }
        public string ConnectionId { get; set; }
        public DateTime ConnectedAt { get; set; }
        public DateTime? DisconnectedAt { get; set; }
        public bool IsActive { get; set; }
    }
}