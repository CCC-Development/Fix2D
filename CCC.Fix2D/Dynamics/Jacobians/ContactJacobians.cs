﻿using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace CCC.Fix2D
{
    public struct ContactJacobianAngular
    {
        public float3 AngularA;
        public float3 AngularB;
        public float EffectiveMass;
        public float Impulse; // Accumulated impulse
    }

    public struct ContactJacAngAndVelToReachCp  // TODO: better name
    {
        public ContactJacobianAngular Jac;

        // Velocity needed to reach the contact plane in one frame,
        // both if approaching (negative) and depenetrating (positive)
        public float VelToReachCp;
    }

    public struct MassFactors
    {
        public float InverseInertiaFactorA;
        public float InverseMassFactorA;
        public float InverseInertiaFactorB;
        public float InverseMassFactorB;

        public static MassFactors Default => new MassFactors
        {
            InverseInertiaFactorA = 1.0f,
            InverseMassFactorA = 1.0f,
            InverseInertiaFactorB = 1.0f,
            InverseMassFactorB = 1.0f
        };
    }

    struct BaseContactJacobian
    {
        public int NumContacts;
        public float2 Normal;

        internal static float GetJacVelocity(float2 linear, ContactJacobianAngular jacAngular,
            float2 linVelA, float angVelA, float2 linVelB, float angVelB)
        {
            return GetJacVelocity(
                math.float3(linear, 0), jacAngular,
                math.float3(linVelA, 0), math.float3(0, 0, angVelA),
                math.float3(linVelB, 0), math.float3(0, 0, angVelB));
        }

        internal static float GetJacVelocity(float3 linear, ContactJacobianAngular jacAngular,
            float3 linVelA, float3 angVelA, float3 linVelB, float3 angVelB)
        {
            float3 temp = (linVelA - linVelB) * linear;
            temp += angVelA * jacAngular.AngularA;
            temp += angVelB * jacAngular.AngularB;
            return math.csum(temp);
        }
    }

    public struct SurfaceVelocity
    {
        // Velocities between the two contacting objects
        public float3 LinearVelocity;
        public float AngularVelocity;
    }

    // A Jacobian representing a set of contact points that apply impulses
    [NoAlias]
    struct ContactJacobian
    {
        public BaseContactJacobian BaseJacobian;

        // Linear friction jacobians.  Only store the angular part, linear part can be recalculated from BaseJacobian.Normal 
        public ContactJacobianAngular Friction0; // EffectiveMass stores friction effective mass matrix element (0, 0)
        public ContactJacobianAngular Friction1; // EffectiveMass stores friction effective mass matrix element (1, 1)

        // Angular friction about the contact normal, no linear part
        public ContactJacobianAngular AngularFriction; // EffectiveMass stores friction effective mass matrix element (2, 2)
        public float3 FrictionEffectiveMassOffDiag; // Effective mass matrix (0, 1), (0, 2), (1, 2) == (1, 0), (2, 0), (2, 1)

        public float CoefficientOfFriction;

        // Generic solve method that dispatches to specific ones
        public void Solve(
            ref JacobianHeader jacHeader, ref PhysicsBody.MotionVelocity velocityA, ref PhysicsBody.MotionVelocity velocityB,
            Solver.StepInput stepInput, ref NativeStream.Writer collisionEventsWriter, bool enableFrictionVelocitiesHeuristic,
            Solver.MotionStabilizationInput motionStabilizationSolverInputA, Solver.MotionStabilizationInput motionStabilizationSolverInputB)
        {
            bool bothBodiesWithInfInertiaAndMass = velocityA.HasInfiniteInertiaAndMass && velocityB.HasInfiniteInertiaAndMass;
            if (bothBodiesWithInfInertiaAndMass)
            {
                SolveInfMassPair(ref jacHeader, velocityA, velocityB, stepInput, ref collisionEventsWriter);
            }
            else
            {
                SolveContact(ref jacHeader, ref velocityA, ref velocityB, stepInput, ref collisionEventsWriter,
                    enableFrictionVelocitiesHeuristic, motionStabilizationSolverInputA, motionStabilizationSolverInputB);
            }
        }

        // Solve the Jacobian
        public void SolveContact(
            ref JacobianHeader jacHeader, ref PhysicsBody.MotionVelocity velocityA, ref PhysicsBody.MotionVelocity velocityB,
            Solver.StepInput stepInput, ref NativeStream.Writer collisionEventsWriter, bool enableFrictionVelocitiesHeuristic,
            Solver.MotionStabilizationInput motionStabilizationSolverInputA, Solver.MotionStabilizationInput motionStabilizationSolverInputB)
        {
            // Copy velocity data
            PhysicsBody.MotionVelocity tempVelocityA = velocityA;
            PhysicsBody.MotionVelocity tempVelocityB = velocityB;
            if (jacHeader.HasMassFactors)
            {
                MassFactors jacMod = jacHeader.AccessMassFactors();
                tempVelocityA.InverseInertia *= jacMod.InverseInertiaFactorA;
                tempVelocityA.InverseMass *= jacMod.InverseMassFactorA;
                tempVelocityB.InverseInertia *= jacMod.InverseInertiaFactorB;
                tempVelocityB.InverseMass *= jacMod.InverseMassFactorB;
            }

            // Solve normal impulses
            float sumImpulses = 0.0f;
            float totalAccumulatedImpulse = 0.0f;
            bool forceCollisionEvent = false;
            for (int j = 0; j < BaseJacobian.NumContacts; j++)
            {
                ref ContactJacAngAndVelToReachCp jacAngular = ref jacHeader.AccessAngularJacobian(j);

                // Solve velocity so that predicted contact distance is greater than or equal to zero
                float relativeVelocity = BaseContactJacobian.GetJacVelocity(BaseJacobian.Normal, jacAngular.Jac,
                    tempVelocityA.LinearVelocity, tempVelocityA.AngularVelocity, tempVelocityB.LinearVelocity, tempVelocityB.AngularVelocity);
                float dv = jacAngular.VelToReachCp - relativeVelocity;

                float impulse = dv * jacAngular.Jac.EffectiveMass;
                float accumulatedImpulse = math.max(jacAngular.Jac.Impulse + impulse, 0.0f);
                if (accumulatedImpulse != jacAngular.Jac.Impulse)
                {
                    float deltaImpulse = accumulatedImpulse - jacAngular.Jac.Impulse;
                    ApplyImpulse(deltaImpulse, BaseJacobian.Normal, jacAngular.Jac, ref tempVelocityA, ref tempVelocityB,
                        motionStabilizationSolverInputA.InverseInertiaScale, motionStabilizationSolverInputB.InverseInertiaScale);
                }

                jacAngular.Jac.Impulse = accumulatedImpulse;
                sumImpulses += accumulatedImpulse;
                totalAccumulatedImpulse += jacAngular.Jac.Impulse;

                // Force contact event even when no impulse is applied, but there is penetration.
                forceCollisionEvent |= jacAngular.VelToReachCp > 0.0f;
            }

            // Export collision event
            if (stepInput.IsLastIteration && (totalAccumulatedImpulse > 0.0f || forceCollisionEvent) && jacHeader.HasContactManifold)
            {
                ExportCollisionEvent(totalAccumulatedImpulse, ref jacHeader, ref collisionEventsWriter);
            }

            // Solve friction
            if (sumImpulses > 0.0f)
            {
                float3 normal3d = math.float3(BaseJacobian.Normal, 0);

                // Choose friction axes
                PhysicsMath.CalculatePerpendicularNormalized(normal3d, out float3 frictionDir0, out float3 frictionDir1);

                // Calculate impulses for full stop
                float3 imp;
                {
                    // Take velocities that produce minimum energy (between input and solver velocity) as friction input
                    float3 frictionLinVelA = math.float3(tempVelocityA.LinearVelocity, 0);
                    float3 frictionAngVelA = math.float3(0, 0, tempVelocityA.AngularVelocity);
                    float3 frictionLinVelB = math.float3(tempVelocityB.LinearVelocity, 0);
                    float3 frictionAngVelB = math.float3(0, 0, tempVelocityB.AngularVelocity);
                    if (enableFrictionVelocitiesHeuristic)
                    {
                        GetFrictionVelocities(new float3(motionStabilizationSolverInputA.InputVelocity.Linear, 0), motionStabilizationSolverInputA.InputVelocity.Angular,
                            new float3(tempVelocityA.LinearVelocity, 0), tempVelocityA.AngularVelocity,
                            math.rcp(tempVelocityA.InverseInertia), math.rcp(tempVelocityA.InverseMass),
                            out frictionLinVelA, out frictionAngVelA);
                        GetFrictionVelocities(new float3(motionStabilizationSolverInputB.InputVelocity.Linear, 0), motionStabilizationSolverInputB.InputVelocity.Angular,
                            new float3(tempVelocityB.LinearVelocity, 0), tempVelocityB.AngularVelocity,
                            math.rcp(tempVelocityB.InverseInertia), math.rcp(tempVelocityB.InverseMass),
                            out frictionLinVelB, out frictionAngVelB);
                    }

                    float3 extraFrictionDv = float3.zero;
                    if (jacHeader.HasSurfaceVelocity)
                    {
                        var surfVel = jacHeader.AccessSurfaceVelocity();

                        PhysicsMath.CalculatePerpendicularNormalized(normal3d, out float3 dir0, out float3 dir1);
                        float linVel0 = math.dot(surfVel.LinearVelocity, dir0);
                        float linVel1 = math.dot(surfVel.LinearVelocity, dir1);

                        float angVelProj = math.dot(surfVel.AngularVelocity, normal3d);
                        extraFrictionDv = new float3(linVel0, linVel1, angVelProj);
                    }

                    // Calculate the jacobian dot velocity for each of the friction jacobians
                    float dv0 = extraFrictionDv.x - BaseContactJacobian.GetJacVelocity(frictionDir0, Friction0, frictionLinVelA, frictionAngVelA, frictionLinVelB, frictionAngVelB);
                    float dv1 = extraFrictionDv.y - BaseContactJacobian.GetJacVelocity(frictionDir1, Friction1, frictionLinVelA, frictionAngVelA, frictionLinVelB, frictionAngVelB);
                    float dva = extraFrictionDv.z - math.csum(AngularFriction.AngularA * frictionAngVelA + AngularFriction.AngularB * frictionAngVelB);

                    // Reassemble the effective mass matrix
                    float3 effectiveMassDiag = new float3(Friction0.EffectiveMass, Friction1.EffectiveMass, AngularFriction.EffectiveMass);
                    float3x3 effectiveMass = JacobianUtilities.BuildSymmetricMatrix(effectiveMassDiag, FrictionEffectiveMassOffDiag);

                    // Calculate the impulse
                    imp = math.mul(effectiveMass, new float3(dv0, dv1, dva));
                }

                // Clip TODO.ma calculate some contact radius and use it to influence balance between linear and angular friction
                float maxImpulse = sumImpulses * CoefficientOfFriction * stepInput.InvNumSolverIterations;
                float frictionImpulseSquared = math.lengthsq(imp);
                imp *= math.min(1.0f, maxImpulse * math.rsqrt(frictionImpulseSquared));

                // Apply impulses
                ApplyImpulse(imp.x, frictionDir0.xy, Friction0, ref tempVelocityA, ref tempVelocityB,
                    motionStabilizationSolverInputA.InverseInertiaScale, motionStabilizationSolverInputB.InverseInertiaScale);
                ApplyImpulse(imp.y, frictionDir1.xy, Friction1, ref tempVelocityA, ref tempVelocityB,
                    motionStabilizationSolverInputA.InverseInertiaScale, motionStabilizationSolverInputB.InverseInertiaScale);

                tempVelocityA.ApplyAngularImpulse((imp.z * AngularFriction.AngularA * motionStabilizationSolverInputA.InverseInertiaScale).z);
                tempVelocityB.ApplyAngularImpulse((imp.z * AngularFriction.AngularB * motionStabilizationSolverInputB.InverseInertiaScale).z);

                // Accumulate them
                Friction0.Impulse += imp.x;
                Friction1.Impulse += imp.y;
                AngularFriction.Impulse += imp.z;
            }

            // Write back linear and angular velocities. Changes to other properties, like InverseMass, should not be persisted.
            velocityA.LinearVelocity = tempVelocityA.LinearVelocity;
            velocityA.AngularVelocity = tempVelocityA.AngularVelocity;
            velocityB.LinearVelocity = tempVelocityB.LinearVelocity;
            velocityB.AngularVelocity = tempVelocityB.AngularVelocity;
        }

        // Solve the infinite mass pair Jacobian
        public void SolveInfMassPair(
            ref JacobianHeader jacHeader, PhysicsBody.MotionVelocity velocityA, PhysicsBody.MotionVelocity velocityB,
            Solver.StepInput stepInput, ref NativeStream.Writer collisionEventsWriter)
        {
            // Infinite mass pairs are only interested in collision events,
            // so only last iteration is performed in that case
            if (!stepInput.IsLastIteration)
            {
                return;
            }

            // Calculate normal impulses and fire collision event
            // if at least one contact point would have an impulse applied
            for (int j = 0; j < BaseJacobian.NumContacts; j++)
            {
                ref ContactJacAngAndVelToReachCp jacAngular = ref jacHeader.AccessAngularJacobian(j);

                float relativeVelocity = BaseContactJacobian.GetJacVelocity(BaseJacobian.Normal, jacAngular.Jac,
                    velocityA.LinearVelocity, velocityA.AngularVelocity, velocityB.LinearVelocity, velocityB.AngularVelocity);
                float dv = jacAngular.VelToReachCp - relativeVelocity;
                if (jacAngular.VelToReachCp > 0 || dv > 0)
                {
                    // Export collision event only if impulse would be applied, or objects are penetrating
                    ExportCollisionEvent(0.0f, ref jacHeader, ref collisionEventsWriter);

                    return;
                }
            }
        }

        // Helper functions
        private static void GetFrictionVelocities(
            float3 inputLinearVelocity, float3 inputAngularVelocity,
            float3 intermediateLinearVelocity, float3 intermediateAngularVelocity,
            float3 inertia, float mass,
            out float3 frictionLinearVelocityOut, out float3 frictionAngularVelocityOut)
        {
            float inputEnergy;
            {
                float linearEnergySq = mass * math.lengthsq(inputLinearVelocity);
                float angularEnergySq = math.dot(inertia * inputAngularVelocity, inputAngularVelocity);
                inputEnergy = linearEnergySq + angularEnergySq;
            }

            float intermediateEnergy;
            {
                float linearEnergySq = mass * math.lengthsq(intermediateLinearVelocity);
                float angularEnergySq = math.dot(inertia * intermediateAngularVelocity, intermediateAngularVelocity);
                intermediateEnergy = linearEnergySq + angularEnergySq;
            }

            if (inputEnergy < intermediateEnergy)
            {
                // Make sure we don't change the sign of intermediate velocity when using the input one.
                // If sign was to be changed, zero it out since it produces less energy.
                bool3 changedSignLin = inputLinearVelocity * intermediateLinearVelocity < float3.zero;
                bool3 changedSignAng = inputAngularVelocity * intermediateAngularVelocity < float3.zero;
                frictionLinearVelocityOut = math.select(inputLinearVelocity, float3.zero, changedSignLin);
                frictionAngularVelocityOut = math.select(inputAngularVelocity, float3.zero, changedSignAng);
            }
            else
            {
                frictionLinearVelocityOut = intermediateLinearVelocity;
                frictionAngularVelocityOut = intermediateAngularVelocity;
            }
        }

        private static void ApplyImpulse(
            float impulse, float2 linear, ContactJacobianAngular jacAngular,
            ref PhysicsBody.MotionVelocity velocityA, ref PhysicsBody.MotionVelocity velocityB,
            float inverseInertiaScaleA = 1.0f, float inverseInertiaScaleB = 1.0f)
        {
            velocityA.ApplyLinearImpulse(impulse * linear);
            velocityB.ApplyLinearImpulse(-impulse * linear);

            // Scale the impulse with inverseInertiaScale
            velocityA.ApplyAngularImpulse(impulse * jacAngular.AngularA.z * inverseInertiaScaleA);
            velocityB.ApplyAngularImpulse(impulse * jacAngular.AngularB.z * inverseInertiaScaleB);
        }

        private unsafe void ExportCollisionEvent(float totalAccumulatedImpulse, [NoAlias] ref JacobianHeader jacHeader,
            [NoAlias] ref NativeStream.Writer collisionEventsWriter)
        {
            // Write size before every event
            int collisionEventSize = CollisionEventData.CalculateSize(BaseJacobian.NumContacts);
            collisionEventsWriter.Write(collisionEventSize);

            // Allocate all necessary data for this event
            byte* eventPtr = collisionEventsWriter.Allocate(collisionEventSize);

            // Fill up event data
            ref CollisionEventData collisionEvent = ref UnsafeUtility.AsRef<CollisionEventData>(eventPtr);
            collisionEvent.BodyIndices = jacHeader.BodyPair;
            collisionEvent.ColliderKeys = jacHeader.AccessColliderKeys();
            collisionEvent.Entities = jacHeader.AccessEntities();
            collisionEvent.Normal = BaseJacobian.Normal;
            collisionEvent.SolverImpulse = totalAccumulatedImpulse;
            collisionEvent.NumNarrowPhaseContactPoints = BaseJacobian.NumContacts;
            for (int i = 0; i < BaseJacobian.NumContacts; i++)
            {
                collisionEvent.AccessContactPoint(i) = jacHeader.AccessContactPoint(i);
            }
        }
    }
    // A Jacobian representing a set of contact points that export trigger events
    [NoAlias]
    struct TriggerJacobian
    {
        public BaseContactJacobian BaseJacobian;
        public ColliderKeyPair ColliderKeys;
        public EntityPair Entities;

        // Solve the Jacobian
        public void Solve(
            ref JacobianHeader jacHeader, ref PhysicsBody.MotionVelocity velocityA, ref PhysicsBody.MotionVelocity velocityB, Solver.StepInput stepInput,
            ref NativeStream.Writer triggerEventsWriter)
        {
            // Export trigger events only in last iteration
            if (!stepInput.IsLastIteration)
            {
                return;
            }

            for (int j = 0; j < BaseJacobian.NumContacts; j++)
            {
                ref ContactJacAngAndVelToReachCp jacAngular = ref jacHeader.AccessAngularJacobian(j);

                // Solve velocity so that predicted contact distance is greater than or equal to zero
                float relativeVelocity = BaseContactJacobian.GetJacVelocity(BaseJacobian.Normal, jacAngular.Jac,
                    velocityA.LinearVelocity, velocityA.AngularVelocity, velocityB.LinearVelocity, velocityB.AngularVelocity);
                float dv = jacAngular.VelToReachCp - relativeVelocity;
                if (jacAngular.VelToReachCp > 0 || dv > 0)
                {
                    // Export trigger event only if impulse would be applied, or objects are penetrating
                    triggerEventsWriter.Write(new TriggerEventData
                    {
                        BodyIndices = jacHeader.BodyPair,
                        ColliderKeys = ColliderKeys,
                        Entities = Entities
                    });

                    return;
                }
            }
        }
    }
}