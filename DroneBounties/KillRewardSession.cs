using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace DroneBounties
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    partial class KillRewardSession : MySessionComponentBase
    {
        private const int DamageEventExpirationInSeconds = 300;
        
        private List<IMyPlayer> Players = new List<IMyPlayer>();
        private Dictionary<long, List<GridDamageEvent>> _gridDamageEvents;

        //TODO: Make this externally configurable
        private List<string> _validSubtypeIds = new List<string> { "RivalAIRemoteControlLarge", "RivalAIRemoteControlSmall" };

        public override void LoadData()
        {
            if (!MyAPIGateway.Session.IsServer) return;

            _gridDamageEvents = new Dictionary<long, List<GridDamageEvent>>();

            MyVisualScriptLogicProvider.BlockDamaged += OnBlockDamaged;
            MyVisualScriptLogicProvider.BlockDestroyed += OnBlockDestroyed;

            MyLog.Default.WriteLine("PvE.KillRewardSession: loaded...");
        }
        protected override void UnloadData()
        {
            MyVisualScriptLogicProvider.BlockDamaged -= OnBlockDamaged;
            MyVisualScriptLogicProvider.BlockDestroyed -= OnBlockDestroyed;
        }

        private void OnBlockDestroyed(string entityName, string gridName, string typeId, string subtypeId)
        {
            //No attackerId, so instead we track list of attackers in OnDamage and they're damage in a dictionary and then assign rewards on "kill" appropriately
            
            //Janitorial things first, cleanup expired damage events
            CleanupExpiredDamageEvents(); //I liked having this in the finally but then we had to manually cleanup in the code below, DRY

            try
            {
                if (entityName.Length == 0) return;

                //TODO: Verify if this is simply returning the block type, or the actual instance?
                var entity = MyAPIGateway.Entities.GetEntityByName(entityName);
                if (entity == null) return;

                //Require victim block to be a remote control, and specifically one of the Rival AI ones (by default)
                var victimRemoteBlock = (entity as IMyRemoteControl);
                if (victimRemoteBlock == null || !_validSubtypeIds.Contains(subtypeId)) return;

                //Test if we are even tracking damage events for the target grid, no point doing any extra work. This value is used later though.
                var victimGridEntityId = victimRemoteBlock.EntityId;
                if (!_gridDamageEvents.ContainsKey(victimGridEntityId))
                {
                    //unable to find any damage events for this grid. They either expired or exploded for some other reason.
                    return;
                }

                //Fetch grid reference
                var victimCubeGrid = victimRemoteBlock.CubeGrid;
                if (victimCubeGrid == null) return;

                //TODO: CustomData stuff only works with specific types of functional blocks, should narrow that down in the future instead of hard coding remote controls
                //Load bounty parameters from the custom data of the block
                bool successful = true;
                string result = "";
                
                //TODO: verbose, but preparing for adding additional parameters
                //Block/grid self-defines bounty
                int bountyOnKill = 0;
                if (CustomDataToConfig(victimRemoteBlock.CustomData, ref result, "BountyOnKill"))
                {
                    successful &= int.TryParse(result, out bountyOnKill);
                }

                if (!successful)
                {
                    //Hiding these log lines, could be spammy if most blocks don't have bounties configured??
                    //MyLog.Default.WriteLine("Unable to find bounty info in customData");
                    //MyLog.Default.WriteLine($"customData={victimRemoteBlock.CustomData}");
                    //TODO: optionally, don't bail and assign using a default bounty multiplier TBD.
                    return;
                }

                //For relations checks, we use first majority owner.
                //TODO: Figure out what this is returning. A player identity? A faction ID?
                var victimIdentityId = (victimCubeGrid.BigOwners != null && victimCubeGrid.BigOwners.Count > 0) ? victimCubeGrid.BigOwners[0] : 0L;

                //TODO: Determine if it is really necessary to do this on every event? Is there not a better way to keep it in sync?
                Players.Clear();
                MyAPIGateway.Multiplayer.Players.GetPlayers(Players);

                //Verified above that _gridDamageEvents contains the key victimGridEntityId
                List<GridDamageEvent> gridDamageEvents;
                if (!_gridDamageEvents.TryGetValue(victimGridEntityId, out gridDamageEvents))
                {
                    MyLog.Default.WriteLine("Unable to access grid events. Concurrency issue?");
                    //TODO: Realistic issue?
                    return;
                }

                //Group by attacker, track their cumulative damage
                var damageByAttackers = gridDamageEvents.GroupBy(de => de.AttackerIdentityId, (attackerId, damageEvents) => new
                {
                    AttackerIdentityId = attackerId,
                    CumulativeDamage = damageEvents.Sum(de2 => de2.Damage),
                    Player = Players.SingleOrDefault(_player => _player?.IdentityId == attackerId)
                });

                //Filter out friendly fire
                var validAttackers = damageByAttackers.Where(attacker =>
                {
                    var attackerIdentityId = attacker.AttackerIdentityId;
                    var relation = MyIDModule.GetRelationPlayerPlayer(attackerIdentityId, victimIdentityId);
                    return attacker.Player != null && relation == MyRelationsBetweenPlayers.Enemies;
                });

                //This is how much damage was done to the grid in total by valid attackers within the last DamageEventExpirationInSeconds
                var totalDamage = validAttackers.Sum(va => va.CumulativeDamage);

                //Figure out the proportion of total damage done by each attacker and assign their proportion of the monies
                foreach (var attacker in validAttackers)
                {
                    var proportionDamage = attacker.CumulativeDamage / totalDamage;
                    attacker.Player.RequestChangeBalance((long)(proportionDamage * bountyOnKill));

                    //TODO: Blast out some sort of message to everyone?
                }

                //In theory we shouldn't have to track this grid anymore, there should only be a single trophy block per grid (we're assuming)
                _gridDamageEvents.Remove(victimGridEntityId);
            }
            catch(Exception ex)
            {
                MyLog.Default.WriteLine($"PvE.KillReward: {ex}");
            }
        }

        private void OnBlockDamaged(string entityName, string gridName, string typeId, string subtypeId, float damage, string damageType, long attackerId)
        {
            try
            {
                if (entityName.Length == 0) return;

                //TODO: Verify if this is simply returning the block type, or the actual instance?
                var entity = MyAPIGateway.Entities.GetEntityByName(entityName);
                if (entity == null) return;

                var victimCubeGrid = (entity as IMyCubeBlock)?.CubeGrid;
                if (victimCubeGrid == null) return;

                //TODO: I'm concerned about grabbing the list of players too frequently, however, we *do* need to get the attacker Id in a timely fashion at 
                // the time of damage because I believe it's looking at who's in the turret and what not and that could change by the time the grid is killed
                // However, if we aren't concerned about that, this could all be done in onBlockDestroyed
                Players.Clear();
                MyAPIGateway.Multiplayer.Players.GetPlayers(Players);
                IMyPlayer attackingPlayer = Players.SingleOrDefault(_player => _player.Character != null && _player.Character.EntityId == attackerId);
                var attackerIdentityId = (attackingPlayer == null) ? GetAttackerIdentityId(attackerId) : attackingPlayer.IdentityId;

                GridDamageEvent damageEvent = new GridDamageEvent()
                {
                    AttackerIdentityId = attackerIdentityId, // I did the damage
                    VictimEntityId = victimCubeGrid.EntityId, // I was damaged
                    Damage = damage, // How much damage
                    TimestampTicks = DateTime.Now.Ticks // When the damage was done
                };

                List<GridDamageEvent> gridDamageEvents = null;
                if (_gridDamageEvents.ContainsKey(victimCubeGrid.EntityId))
                {
                    if(!_gridDamageEvents.TryGetValue(victimCubeGrid.EntityId, out gridDamageEvents))
                    {
                        MyLog.Default.WriteLine("Unable to access grid events. Concurrency issue?");
                        //TODO: Realistic issue?
                    }
                }

                if (gridDamageEvents != null)
                {
                    gridDamageEvents.Add(damageEvent);
                }
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"PvE.KillReward: {ex}");
            }
            finally
            {
                CleanupExpiredDamageEvents();
            }
        }

        /// <summary>
        /// Purges expired damage events
        /// </summary>
        private void CleanupExpiredDamageEvents()
        {
            var ticksDeadline = DateTime.Now.Add(TimeSpan.FromSeconds(-DamageEventExpirationInSeconds)).Ticks;
            foreach (var gridEntity in _gridDamageEvents)
            {
                var gridDamageEvents = gridEntity.Value;
                gridDamageEvents.RemoveAll(de => de.TimestampTicks > ticksDeadline);
            }

            //TODO: remove grids with empty damage event lists
        }

        #region Helper Statics
        internal static bool CustomDataToConfig(string input, ref string output, string search) 
        {
            //Keep method static and regex cache will re-use compiled version
            MatchCollection matchcollection = Regex.Matches(input, search + @"\s?=\s?(.+?);", RegexOptions.Compiled);
            foreach (Match match in matchcollection)
            {
                if (match.Groups.Count == 2)
                {
                    output = (match.Groups[1].Value.Trim());
                    return true;
                }
                else
                    return false;
            }
            return false;
        }

        public static bool IsIdentityPlayer(long id)
        {
            return MyAPIGateway.Players.TryGetSteamId(id) > 0;
        }

        // Borrowed from: https://steamcommunity.com/sharedfiles/filedetails/?id=2495746295 Unsure if they were original author of this method.
        // My understanding is it's getting the identity of the person controlling a block that has done the damage, or it's the identity of the charcter holding the gun
        internal static long GetAttackerIdentityId(long attackerId) 
        {
            var entity = MyAPIGateway.Entities.GetEntityById(attackerId);
            var myControllableEntity = entity as VRage.Game.ModAPI.Interfaces.IMyControllableEntity;
            var cubeGrid = entity as IMyCubeGrid;

            if (cubeGrid != null) return (cubeGrid.BigOwners.Count > 0) ? cubeGrid.BigOwners[0] : 0;

            if (myControllableEntity != null)
            {
                var controllerInfo = myControllableEntity.ControllerInfo;

                if (controllerInfo != null) return controllerInfo.ControllingIdentityId;
            }
            else
            {
                IMyGunBaseUser myGunBaseUser;
                if ((myGunBaseUser = (entity as IMyGunBaseUser)) != null) return myGunBaseUser.OwnerId;

                IMyHandheldGunObject<MyDeviceBase> myHandheldGunObject;
                if ((myHandheldGunObject = (entity as IMyHandheldGunObject<MyDeviceBase>)) != null) return myHandheldGunObject.OwnerIdentityId;
            }

            return 0L;
        }
        #endregion
    }
}
