using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace OnlineFPS
{

    [Serializable]
    public class RenderEnvironment : IComponentData
    {
        public int LightingSceneIndex;
    }

    [Serializable]
    public class RenderEnvironmentCleanup : ICleanupComponentData
    {
        public int LightingSceneIndex;
    }
}
