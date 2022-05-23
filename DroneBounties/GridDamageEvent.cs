namespace DroneBounties
{
    internal class GridDamageEvent
    {
        //Not guaranteed to be "enemies", that is verified on kill
        public long AttackerIdentityId { get; set; }
        public long VictimEntityId { get; set; } 
        public float Damage { get; set; }
        public long TimestampTicks { get; set; }
    }
}
