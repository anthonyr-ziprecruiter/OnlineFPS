using System;
using System.Windows.Input;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace OnlineFPS
{

    [Serializable]
    [GhostComponent()]
    public struct FirstPersonPlayer : IComponentData
    {
        [GhostField()]
        public FixedString128Bytes Name;
        [GhostField()]
        public Entity ControlledCharacter;

        public bool IsAutoMoving;
    }

    [Serializable]
    public struct FirstPersonPlayerCommands : IInputComponentData
    {
        public float2 MoveInput;
        public float2 LookInputDelta;
        public InputEvent JumpPressed;
        public InputEvent ShootPressed;
        public InputEvent ShootReleased;
        public bool AimHeld;
    }
}
