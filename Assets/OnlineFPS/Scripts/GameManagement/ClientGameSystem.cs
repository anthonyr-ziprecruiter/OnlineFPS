using System.Collections;
using System.Collections.Generic;
using Unity.Burst;
using Unity.CharacterController;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Scenes;
using Unity.Transforms;
using UnityEngine;

namespace OnlineFPS
{

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [BurstCompile]
    public partial struct ClientGameSystem : ISystem
    {
        public struct Singleton : IComponentData
        {
            public Unity.Mathematics.Random Random;

            public float TimeWithoutAConnection;
            public bool Spectator;

            public int DisconnectionFramesCounter;
        }

        public struct JoinOnceScenesLoadedRequest : IComponentData
        {
            public Entity PendingSceneLoadRequest;
        }

        public struct JoinRequest : IRpcCommand
        {
            public FixedString128Bytes PlayerName;
            public bool Spectator;
        }

        public struct DisconnectRequest : IComponentData
        { }

        private EntityQuery _singletonQuery;
        private EntityQuery _spectatorSpawnPointsQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameResources>();

            _singletonQuery = new EntityQueryBuilder(Allocator.Temp).WithAllRW<Singleton>().Build(state.EntityManager);
            _spectatorSpawnPointsQuery = new EntityQueryBuilder(Allocator.Temp).WithAll<SpectatorSpawnPoint, LocalToWorld>().Build(state.EntityManager);

            // Auto-create singleton
            Entity singletonEntity = state.EntityManager.CreateEntity();
            state.EntityManager.AddComponentData(singletonEntity, new Singleton
            {
                Random = Unity.Mathematics.Random.CreateFromIndex(0),
            });
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            ref Singleton singleton = ref _singletonQuery.GetSingletonRW<Singleton>().ValueRW;
            GameResources gameResources = SystemAPI.GetSingleton<GameResources>();

            if (SystemAPI.HasSingleton<DisableCharacterDynamicContacts>())
            {
                state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<DisableCharacterDynamicContacts>());
            }

            HandleSendJoinRequestOncePendingScenesLoaded(ref state, ref singleton);
            HandlePendingJoinRequest(ref state, ref singleton, gameResources);
            HandleCharacterSetupAndDestruction(ref state);
            HandleDisconnect(ref state, ref singleton, gameResources);
            HandleRespawnScreen(ref state, ref singleton, gameResources);
        }

        private void HandleSendJoinRequestOncePendingScenesLoaded(ref SystemState state, ref Singleton singleton)
        {
            EntityCommandBuffer ecb = SystemAPI.GetSingletonRW<BeginSimulationEntityCommandBufferSystem.Singleton>().ValueRW.CreateCommandBuffer(state.WorldUnmanaged);

            foreach (var (request, entity) in SystemAPI.Query<JoinOnceScenesLoadedRequest>().WithEntityAccess())
            {
                if (SystemAPI.HasComponent<SceneLoadRequest>(request.PendingSceneLoadRequest) && SystemAPI.GetComponent<SceneLoadRequest>(request.PendingSceneLoadRequest).IsLoaded)
                {
                    LocalGameData localData = SystemAPI.GetSingleton<LocalGameData>();

                    // Send join request
                    if (SystemAPI.HasSingleton<NetworkId>())
                    {
                        Entity joinRequestEntity = ecb.CreateEntity();
                        ecb.AddComponent(joinRequestEntity, new JoinRequest
                        {
                            PlayerName = localData.LocalPlayerName,
                            Spectator = singleton.Spectator,
                        });
                        ecb.AddComponent(joinRequestEntity, new SendRpcCommandRequest());

                        ecb.DestroyEntity(request.PendingSceneLoadRequest);
                        ecb.DestroyEntity(entity);
                    }
                }
            }
        }

        private void HandlePendingJoinRequest(ref SystemState state, ref Singleton singleton, GameResources gameResources)
        {
            EntityCommandBuffer ecb = SystemAPI.GetSingletonRW<BeginSimulationEntityCommandBufferSystem.Singleton>().ValueRW.CreateCommandBuffer(state.WorldUnmanaged);

            if (SystemAPI.HasSingleton<NetworkId>() && !SystemAPI.HasSingleton<NetworkStreamInGame>())
            {
                singleton.TimeWithoutAConnection = 0f;

                // Check for request accept
                foreach (var (requestAccepted, rpcReceive, entity) in SystemAPI.Query<ServerGameSystem.JoinRequestAccepted, ReceiveRpcCommandRequest>().WithEntityAccess())
                {
                    // Stream in game
                    ecb.AddComponent(SystemAPI.GetSingletonEntity<NetworkId>(), new NetworkStreamInGame());

                    // Spectator mode
                    if (singleton.Spectator)
                    {
                        LocalToWorld spawnPoint = default;
                        NativeArray<LocalToWorld> spectatorSpawnPoints = _spectatorSpawnPointsQuery.ToComponentDataArray<LocalToWorld>(Allocator.Temp);
                        if (spectatorSpawnPoints.Length > 0)
                        {
                            spawnPoint = spectatorSpawnPoints[singleton.Random.NextInt(0, spectatorSpawnPoints.Length - 1)];
                        }

                        Entity spectatorEntity = ecb.Instantiate(gameResources.SpectatorPrefab);
                        ecb.SetComponent(spectatorEntity, new LocalTransform() { Position = spawnPoint.Position, Rotation = spawnPoint.Rotation, Scale = 1f });

                        spectatorSpawnPoints.Dispose();
                    }

                    ecb.DestroyEntity(entity);
                }
            }
        }

        private void HandleCharacterSetupAndDestruction(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<NetworkId>())
            {
                EntityCommandBuffer ecb = SystemAPI.GetSingletonRW<BeginSimulationEntityCommandBufferSystem.Singleton>().ValueRW.CreateCommandBuffer(state.WorldUnmanaged);

                // Initialize local-owned characters
                foreach (var (character, owningPlayer, ghostOwner, entity) in SystemAPI.Query<FirstPersonCharacterComponent, OwningPlayer, GhostOwner>().WithAll<GhostOwnerIsLocal>().WithNone<CharacterInitialized>().WithEntityAccess())
                {
                    // Make camera follow character's view
                    ecb.AddComponent(character.ViewEntity, new MainEntityCamera { BaseFoV = character.BaseFoV });

                    // Make local character meshes rendering be shadow-only
                    BufferLookup<Child> childBufferLookup = SystemAPI.GetBufferLookup<Child>();
                    MiscUtilities.SetShadowModeInHierarchy(state.EntityManager, ecb, entity, ref childBufferLookup, UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly);

                    // Enable crosshair
                    Entity crosshairRequestEntity = ecb.CreateEntity();
                    ecb.AddComponent(crosshairRequestEntity, new CrosshairRequest { Enable = true });
                    ecb.AddComponent(crosshairRequestEntity, new MoveToLocalWorld());

                    // Disable respawn screen (if any)
                    Entity respawnScreenRequestEntity = ecb.CreateEntity();
                    ecb.AddComponent(respawnScreenRequestEntity, new RespawnMessageRequest { Start = false });
                    ecb.AddComponent(respawnScreenRequestEntity, new MoveToLocalWorld());

                    // Mark initialized
                    ecb.AddComponent<CharacterInitialized>(entity);
                }

                // Initialize remote characters
                foreach (var (character, owningPlayer, ghostOwner, entity) in SystemAPI.Query<FirstPersonCharacterComponent, OwningPlayer, GhostOwner>().WithNone<GhostOwnerIsLocal>().WithNone<CharacterInitialized>().WithEntityAccess())
                {
                    // Spawn nameTag
                    ecb.AddComponent(character.NameTagSocketEntity, new NameTagProxy { PlayerEntity = owningPlayer.Entity });

                    // Mark initialized
                    ecb.AddComponent<CharacterInitialized>(entity);
                }
            }
        }

        private void HandleDisconnect(ref SystemState state, ref Singleton singleton, GameResources gameResources)
        {
            EntityCommandBuffer ecb = SystemAPI.GetSingletonRW<BeginSimulationEntityCommandBufferSystem.Singleton>().ValueRW.CreateCommandBuffer(state.WorldUnmanaged);

            // Check for connection timeout
            if (!SystemAPI.HasSingleton<NetworkId>())
            {
                singleton.TimeWithoutAConnection += SystemAPI.Time.DeltaTime;
                if (singleton.TimeWithoutAConnection > gameResources.JoinTimeout)
                {
                    Entity disconnectEntity = ecb.CreateEntity();
                    ecb.AddComponent(disconnectEntity, new DisconnectRequest());
                }
            }

            // Handle disconnecting & properly disposing world
            EntityQuery disconnectRequestQuery = SystemAPI.QueryBuilder().WithAll<DisconnectRequest>().Build();
            if (disconnectRequestQuery.CalculateEntityCount() > 0)
            {
                // Add disconnect request to connection
                foreach (var (connection, entity) in SystemAPI.Query<NetworkId>().WithNone<NetworkStreamRequestDisconnect>().WithEntityAccess())
                {
                    ecb.AddComponent(entity, new NetworkStreamRequestDisconnect());
                }

                // Make sure all renderEnvironments are disposed before initiating disconnect
                EntityQuery renderEnvironmentsQuery = SystemAPI.QueryBuilder().WithAll<RenderEnvironment>().Build();
                if (renderEnvironmentsQuery.CalculateEntityCount() > 0)
                {
                    state.EntityManager.DestroyEntity(renderEnvironmentsQuery);
                    singleton.DisconnectionFramesCounter = 0;
                }

                // Allow systems to have updated since disconnection, for cleanup
                if (singleton.DisconnectionFramesCounter > 3)
                {
                    Entity disposeRequestEntity = ecb.CreateEntity();
                    ecb.AddComponent(disposeRequestEntity, new GameManagementSystem.DisposeClientWorldRequest());
                    ecb.AddComponent(disposeRequestEntity, new MoveToLocalWorld());
                    ecb.DestroyEntity(disconnectRequestQuery, EntityQueryCaptureMode.AtRecord);
                }

                singleton.DisconnectionFramesCounter++;
            }
        }

        private void HandleRespawnScreen(ref SystemState state, ref Singleton singleton, GameResources gameResources)
        {
            EntityCommandBuffer ecb = SystemAPI.GetSingletonRW<BeginSimulationEntityCommandBufferSystem.Singleton>().ValueRW.CreateCommandBuffer(state.WorldUnmanaged);

            foreach (var (respawnScreenRequest, receiveRPC, entity) in SystemAPI.Query<RespawnMessageRequest, ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                // Disable crosshair
                Entity crosshairRequestEntity = ecb.CreateEntity();
                ecb.AddComponent(crosshairRequestEntity, new CrosshairRequest { Enable = false });
                ecb.AddComponent(crosshairRequestEntity, new MoveToLocalWorld());

                // Send request to get processed by UI system
                ecb.AddComponent(entity, new MoveToLocalWorld());
            }
        }
    }
}
