namespace DroneBounties
{
    internal class GridDamagedEvent
    {
        //Not guaranteed to be "enemies", that is verified on kill
        public long AttackerIdentityId { get; set; }
        public long VictimEntityId { get; set; }
        public float Damage { get; set; }
        public long TimestampTicks { get; set; }
    }

    internal class GridDestroyedEvent
    {
        public string VictimGridDisplayName { get; set; }
        public long VictimEntityId { get; set; }
        public long VictimIdentityId { get; set; }
        public int BountyOnKill { get; set; }
    }
}
