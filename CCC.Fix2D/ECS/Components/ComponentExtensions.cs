﻿using Unity.Mathematics;

namespace CCC.Fix2D
{
    public static class ComponentExtensions
    {
        // Get the mass, converting from inverse mass.
        public static float GetMass(this PhysicsMass physicsMass)
        {
            return math.abs(physicsMass.InverseMass) > float.Epsilon ? math.rcp(physicsMass.InverseMass) : 0f;
        }

        // Get the inertia, converting from inverse inertia.
        public static float GetInertia(this PhysicsMass physicsMass)
        {
            return math.abs(physicsMass.InverseInertia) > float.Epsilon ? math.rcp(physicsMass.InverseInertia) : 0f;
        }

        // Apply a linear impulse to the center of mass.
        public static void ApplyLinearImpulse(ref this PhysicsVelocity physicsVelocity, PhysicsMass physicsMass, float2 impulseF)
        {
            fix2 impulse = new fix2((fix)impulseF.x, (fix)impulseF.y);
            physicsVelocity.Linear += impulse * (fix)physicsMass.InverseMass;
        }

        // Apply an angular impulse.
        public static void ApplyAngularImpulse(ref this PhysicsVelocity physicsVelocity, PhysicsMass physicsMass, float impulse)
        {
            physicsVelocity.Angular += (fix)impulse * (fix)physicsMass.InverseInertia;
        }
    }
}
