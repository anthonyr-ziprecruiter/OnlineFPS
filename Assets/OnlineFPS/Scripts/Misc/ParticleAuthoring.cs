using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

namespace OnlineFPS
{

    public class ParticleAuthoring : MonoBehaviour
    {
        public class Baker : Baker<ParticleAuthoring>
        {
            public override void Bake(ParticleAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new Particle());
            }
        }
    }
}
