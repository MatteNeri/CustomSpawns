﻿using CustomSpawns.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CustomSpawns.MCMv3;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.SandBox.CampaignBehaviors;
using TaleWorlds.Engine;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace CustomSpawns.Spawn
{
    class SpawnBehaviour : CampaignBehaviorBase
    {

        #region Data Management

        Data.SpawnDataManager dataManager;

        private int lastRedundantDataUpdate = 0;

        private bool dataGottenAtStart = false;

        public SpawnBehaviour(Data.SpawnDataManager data_manager)
        {
            DynamicSpawnData.FlushSpawnData();
            lastRedundantDataUpdate = 0;
            dataManager = data_manager;
            Data.DataUtils.EnsureWarnIDQUalities(dataManager.Data);
            dataGottenAtStart = false;
        }

        public void GetCurrentData()
        {
            foreach (MobileParty mb in MobileParty.All)
            {
                if (mb == null)
                    return;
                foreach (var dat in dataManager.Data)
                {
                    if (CampaignUtils.IsolateMobilePartyStringID(mb) == dat.PartyTemplate.StringId) //TODO could deal with sub parties in the future as well!
                    {
                        //this be a custom spawns party :O
                        DynamicSpawnData.AddDynamicSpawnData(mb, new CSPartyData(dat, null));
                        dat.IncrementNumberSpawned();
                        UpdateDynamicData(mb);
                        UpdateRedundantDynamicData(mb);
                    }
                }
            }

        }

        public void HourlyCheckData()
        {
            if (lastRedundantDataUpdate < CsSettings.UpdatePartyRedundantDataPerHour + 1) // + 1 to give leeway and make sure every party gets updated. 
            {
                lastRedundantDataUpdate++;
            }
            else
            {
                lastRedundantDataUpdate = 0;
            }

            //Now for data checking?
        }

        public void UpdateDynamicData(MobileParty mb)
        {

        }

        public void UpdateRedundantDynamicData(MobileParty mb)
        {
            DynamicSpawnData.GetDynamicSpawnData(mb).latestClosestSettlement = CampaignUtils.GetClosestHabitedSettlement(mb);
        }

        #endregion


        #region MB API-Registered Behaviours

        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, DailyBehaviour);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, HourlyBehaviour);
            CampaignEvents.HourlyTickPartyEvent.AddNonSerializedListener(this, HourlyPartyBehaviour);
            CampaignEvents.OnPartyRemovedEvent.AddNonSerializedListener(this, OnPartyRemoved);
        }

        public override void SyncData(IDataStore dataStore)
        {

        }

        private bool spawnedToday = false;

        private void HourlyBehaviour()
        {
            if (!dataGottenAtStart)
            {
                GetCurrentData();
                dataGottenAtStart = true;
            }
            HourlyCheckData();
            if (!spawnedToday && Campaign.Current.IsNight)
            {
                RegularSpawn();
                spawnedToday = true;
            }

        }

        //deal with our parties being removed! Also this is more efficient ;)
        private void OnPartyRemoved(PartyBase p)
        {
            MobileParty mb = p.MobileParty;
            if (mb == null)
                return;

            CSPartyData partyData = DynamicSpawnData.GetDynamicSpawnData(mb);
            if (partyData != null)
            {
                partyData.spawnBaseData.DecrementNumberSpawned();
                //this is a custom spawns party!!
                OnPartyDeath(mb, partyData);
                ModDebug.ShowMessage(mb.StringId + " has died at " + partyData.latestClosestSettlement + ", reducing the total number to: " + partyData.spawnBaseData.GetNumberSpawned(), DebugMessageType.DeathTrack);
                DynamicSpawnData.RemoveDynamicSpawnData(mb);
            }
        }

        private void HourlyPartyBehaviour(MobileParty mb)
        {
            if (DynamicSpawnData.GetDynamicSpawnData(mb) == null) //check if it is a custom spawns party
                return;
            UpdateDynamicData(mb);
            if (lastRedundantDataUpdate >= CsSettings.UpdatePartyRedundantDataPerHour)
            {
                UpdateRedundantDynamicData(mb);
            }
            //for now for all
            Economics.PartyEconomicUtils.PartyReplenishFood(mb);
        }

        private void DailyBehaviour()
        {
            spawnedToday = false;
        }

        #endregion

        private void RegularSpawn()
        {
            try
            {
                var list = dataManager.Data;
                Random rand = new Random();
                var isSpawnSoundPlaying = false;
                foreach (Data.SpawnData data in list)
                {
                    int j = 0;
                    for (int i = 0; i < data.RepeatSpawnRolls; i++)
                    {
                        if (data.CanSpawn() && (data.MinimumNumberOfDaysUntilSpawn < (int)Math.Ceiling(Campaign.Current.CampaignStartTime.ElapsedDaysUntilNow)))
                        {
                            if (CsSettings.IsAllSpawnMode || (float)rand.NextDouble() < data.ChanceOfSpawn)
                            {
                                var spawnSettlement = Spawner.GetSpawnSettlement(data, rand);
                                //spawn nao!
                                MobileParty spawnedParty = Spawner.SpawnParty(spawnSettlement, data.SpawnClan, data.PartyTemplate, data.PartyType, new TextObject(data.Name), data.PartyTemplatePrisoner, data.InheritClanFromSettlement);
                                data.IncrementNumberSpawned(); //increment for can spawn and chance modifications
                                //dynamic data registration
                                DynamicSpawnData.AddDynamicSpawnData(spawnedParty, new CSPartyData(data, spawnSettlement));

                                j++;
                                //AI Checks!
                                Spawner.HandleAIChecks(spawnedParty, data, spawnSettlement);
                                //accompanying spawns
                                foreach (var accomp in data.SpawnAlongWith)
                                {
                                    MobileParty juniorParty = Spawner.SpawnParty(spawnSettlement, data.SpawnClan, accomp.templateObject, data.PartyType, new TextObject(accomp.name), data.PartyTemplatePrisoner, data.InheritClanFromSettlement);
                                    Spawner.HandleAIChecks(juniorParty, data, spawnSettlement); //junior party has same AI behaviour as main party. TODO in future add some junior party AI and reconstruction.
                                }
                                //message if available
                                if (data.spawnMessage != null && data.inquiryMessage == null)
                                {
                                    UX.ShowParseSpawnMessage(data.spawnMessage, spawnSettlement.Name.ToString());
                                    if (data.SoundEvent != -1 && !isSpawnSoundPlaying && CsSettings.SpawnSoundEnabled)
                                    {
                                        var sceneEmpty = Scene.CreateNewScene(false);
                                        SoundEvent sound = SoundEvent.CreateEvent(data.SoundEvent, sceneEmpty);
                                        sound.Play();
                                        isSpawnSoundPlaying = true;
                                    }
                                }
                                //default spawn message type always takes priority over the inquiry type if they're both present
                                else if (data.spawnMessage != null && data.inquiryMessage != null)
                                {
                                    UX.ShowParseSpawnMessage(data.spawnMessage, spawnSettlement.Name.ToString());
                                }
                                //only if the spawn message EXPLICITLY doesn't exist does it choose the inquiry message
                                else if (data.spawnMessage == null && data.inquiryMessage != null)
                                {
                                    UX.ShowParseSpawnInquiryMessage(data.inquiryMessage, spawnSettlement.Name.ToString(), data.inquiryPause);
                                }
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ErrorHandler.HandleException(e);
            }
        }
        private void OnPartyDeath(MobileParty mb, CSPartyData dynamicData)
        {
            HandleDeathMessage(mb, dynamicData);
        }

        #region Behaviour Handlers

        private void HandleDeathMessage(MobileParty mb, CSPartyData dynamicData)
        {
            if(dynamicData.spawnBaseData.deathMessage != null)
            {
                UX.ShowParseDeathMessage(dynamicData.spawnBaseData.deathMessage, dynamicData.latestClosestSettlement.ToString());
            }
        }

        #endregion
    }
}
