using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace OnlineFPS
{

    public struct DelayedDespawn : IComponentData
    {
        public float Timer;
        public byte HasDisabledRendering;
    }
}
