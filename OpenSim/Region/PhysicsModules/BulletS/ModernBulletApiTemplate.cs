/*
 * Modern Bullet Physics API Template for OpenSim BulletS
 * 
 * This file provides a modernized API template that bridges OpenSim BulletS
 * with modern Bullet Physics 3.x features including:
 * - Multithreaded collision detection
 * - Improved Continuous Collision Detection (CCD)
 * - Enhanced constraint solvers
 * - Better performance optimization
 */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    // Modern Bullet Physics API version targeting Bullet 3.x
    public enum ModernBulletVersion : int
    {
        BULLET_2_82 = 282,
        BULLET_3_17 = 317,  // Target modern version
        BULLET_3_25 = 325   // Latest stable
    }

    // Enhanced physics shape types with modern Bullet 3.x support
    public enum ModernBSPhysicsShapeType
    {
        // Legacy shapes (maintained for compatibility)
        SHAPE_UNKNOWN = 0,
        SHAPE_CAPSULE = 1,
        SHAPE_BOX = 2,
        SHAPE_CONE = 3,
        SHAPE_CYLINDER = 4,
        SHAPE_SPHERE = 5,
        SHAPE_MESH = 6,
        SHAPE_HULL = 7,
        
        // Modern Bullet 3.x shapes
        SHAPE_MULTISPHERE = 8,          // Better sphere approximations
        SHAPE_CONVEX_TRIANGLEMESH = 9,  // Optimized convex meshes
        SHAPE_BVH_TRIANGLEMESH = 10,    // Better triangle mesh handling
        SHAPE_SCALED_TRIANGLE_MESH = 11, // Efficient scaled meshes
        SHAPE_MULTIMATERIAL_TRIANGLE_MESH = 12, // Multiple material support
        
        // BulletSim specific (maintained)
        SHAPE_GROUNDPLANE = 20,
        SHAPE_TERRAIN = 21,
        SHAPE_COMPOUND = 22,
        SHAPE_HEIGHTMAP = 23,
        SHAPE_AVATAR = 24,
        SHAPE_CONVEXHULL = 25,
        SHAPE_GIMPACT = 26,
        
        // Modern extensions
        SHAPE_MODERN_COMPOUND = 27,     // Improved compound shapes
        SHAPE_DEFORMABLE = 28,          // Soft body support
        SHAPE_FLUID = 29                // Fluid simulation shapes
    }

    // Modern collision detection flags
    [Flags]
    public enum ModernCollisionFlags : uint
    {
        // Standard flags
        STATIC_OBJECT = 1,
        KINEMATIC_OBJECT = 2,
        NO_CONTACT_RESPONSE = 4,
        CUSTOM_MATERIAL_CALLBACK = 8,
        CHARACTER_OBJECT = 16,
        DISABLE_VISUALIZE_OBJECT = 32,
        DISABLE_SPU_COLLISION_PROCESSING = 64,
        
        // Modern CCD flags
        USE_CCD = 128,                  // Enable Continuous Collision Detection
        CCD_SWEPT_SPHERE_RADIUS = 256,  // Custom CCD sphere radius
        CCD_MOTION_THRESHOLD = 512,     // Custom CCD motion threshold
        
        // Advanced collision features
        MULTIMATERIAL = 1024,           // Multiple materials per object
        DESTRUCTIBLE = 2048,            // Can be destroyed by impacts
        FLUID_BODY = 4096,              // Fluid dynamics object
        DEFORMABLE = 8192               // Soft body/deformable object
    }

    // Modern constraint types with Bullet 3.x enhancements
    public enum ModernConstraintType : int
    {
        // Legacy constraints (maintained)
        POINT2POINT_CONSTRAINT_TYPE = 3,
        HINGE_CONSTRAINT_TYPE = 4,
        CONETWIST_CONSTRAINT_TYPE = 5,
        D6_CONSTRAINT_TYPE = 6,
        SLIDER_CONSTRAINT_TYPE = 7,
        CONTACT_CONSTRAINT_TYPE = 8,
        D6_SPRING_CONSTRAINT_TYPE = 9,
        GEAR_CONSTRAINT_TYPE = 10,
        FIXED_CONSTRAINT_TYPE = 11,
        
        // Modern Bullet 3.x constraints
        D6_SPRING_2_CONSTRAINT_TYPE = 12,    // Improved 6DOF spring
        MULTIBODY_CONSTRAINT_TYPE = 13,      // Featherstone multibody
        GEAR_MULTIBODY_CONSTRAINT_TYPE = 14, // Multibody gear constraint
        
        MAX_CONSTRAINT_TYPE = 15,
        
        // BulletSim specific
        BS_FIXED_CONSTRAINT_TYPE = 1234,
        BS_MODERN_COMPOUND_CONSTRAINT = 1235  // Modern compound constraint
    }

    // Modern physics material properties
    [StructLayout(LayoutKind.Sequential)]
    public struct ModernPhysicsMaterial
    {
        public float Friction;              // Surface friction coefficient
        public float RollingFriction;       // Rolling resistance (Bullet 3.x)
        public float SpinningFriction;      // Spinning resistance (Bullet 3.x) 
        public float Restitution;           // Bounce/elasticity
        public float Density;               // Material density
        public float FractureStress;        // Stress threshold for destruction
        public uint MaterialFlags;          // Modern material behavior flags
        public Vector3 AnisotropicFriction; // Directional friction (Bullet 3.x)
        
        // Thermal and electromagnetic properties for advanced simulation
        public float ThermalConductivity;
        public float ElectricalConductivity;
        public float MagneticPermeability;
    }

    // Modern collision data with enhanced information
    [StructLayout(LayoutKind.Sequential)]
    public struct ModernCollisionData
    {
        public uint ObjectAID;
        public uint ObjectBID;
        public Vector3 ContactPoint;
        public Vector3 ContactNormal;
        public float ContactDistance;
        public float ContactImpulse;        // Impact force magnitude
        public Vector3 ContactVelocity;     // Relative velocity at contact
        public float ContactArea;           // Contact patch area (Bullet 3.x)
        public uint MaterialAID;            // Material of object A
        public uint MaterialBID;            // Material of object B
        public double Timestamp;            // High-precision collision time
        public uint CollisionFlags;         // Collision behavior flags
        
        // Advanced collision analysis
        public Vector3 FrictionForce;       // Friction force vector
        public Vector3 NormalForce;         // Normal force vector
        public float TangentialImpulse;     // Friction impulse
        public float PenetrationDepth;      // How deep objects penetrated
    }

    // Modern CCD (Continuous Collision Detection) configuration
    [StructLayout(LayoutKind.Sequential)]
    public struct ModernCCDConfig
    {
        public bool EnableCCD;              // Master CCD enable flag
        public float MotionThreshold;       // Minimum motion to trigger CCD
        public float SweptSphereRadius;     // CCD sphere radius
        public int MaxSubSteps;             // Maximum CCD substeps
        public float TimeOfImpact;          // Time of impact calculation precision
        public bool UseConservativeAdvancement; // Conservative advancement algorithm
        public float AllowedPenetration;    // Acceptable penetration depth
        
        // Advanced CCD settings (Bullet 3.x)
        public bool EnableSpeculativeContacts; // Predictive contact generation
        public float ContactBreakingThreshold; // When to break contacts
        public int SolverIterations;        // CCD solver iterations
    }

    // Interface for modern Bullet Physics API
    public interface IModernBulletAPI
    {
        // Version and initialization
        ModernBulletVersion GetBulletVersion();
        bool InitializePhysicsEngine(Vector3 worldSize, int maxObjects);
        void ShutdownPhysicsEngine();
        
        // Modern shape creation with enhanced parameters
        IntPtr CreateModernShape(ModernBSPhysicsShapeType shapeType, 
                               Vector3 size, 
                               ModernPhysicsMaterial material,
                               byte[] meshData = null);
        void DestroyShape(IntPtr shape);
        
        // CCD (Continuous Collision Detection) support
        void SetCCDProperties(IntPtr body, ModernCCDConfig ccdConfig);
        ModernCCDConfig GetCCDProperties(IntPtr body);
        bool EnableCCDForBody(IntPtr body, bool enable);
        
        // Enhanced collision detection
        void SetCollisionFlags(IntPtr body, ModernCollisionFlags flags);
        ModernCollisionFlags GetCollisionFlags(IntPtr body);
        List<ModernCollisionData> GetCollisions(IntPtr body);
        
        // Modern constraint system
        IntPtr CreateModernConstraint(ModernConstraintType type,
                                    IntPtr bodyA, IntPtr bodyB,
                                    Vector3 pivotA, Vector3 pivotB,
                                    Vector3 axisA, Vector3 axisB);
        
        // Multithreaded physics stepping
        void StepSimulationMultithreaded(float timeStep, int maxSubSteps, int threadCount);
        
        // Performance optimization
        void EnableMultithreadedCollisionDetection(bool enable, int threadCount);
        void SetCollisionMargin(IntPtr shape, float margin);
        void OptimizeForLargeWorlds(bool enable);
        
        // Modern material system
        uint CreatePhysicsMaterial(ModernPhysicsMaterial material);
        void UpdatePhysicsMaterial(uint materialID, ModernPhysicsMaterial material);
        ModernPhysicsMaterial GetPhysicsMaterial(uint materialID);
        
        // Advanced features
        void EnableDeformableBodies(bool enable);
        void EnableFluidSimulation(bool enable);
        void SetGravity(Vector3 gravity);
        
        // Debug and profiling
        void EnableDebugDrawing(bool enable);
        string GetPerformanceStatistics();
        void DumpPhysicsState(string filename);
    }

    // Modern physics world configuration
    [StructLayout(LayoutKind.Sequential)]
    public struct ModernPhysicsWorldConfig
    {
        public Vector3 WorldSize;           // Physics world boundaries
        public Vector3 Gravity;             // Gravitational acceleration
        public int MaxObjects;              // Maximum physics objects
        public int MaxConstraints;          // Maximum constraints
        public bool EnableMultithreading;   // Use multiple CPU cores
        public int ThreadCount;             // Number of physics threads
        public bool EnableCCD;              // Global CCD enable
        public bool EnableSoftBodies;       // Soft body simulation
        public bool EnableFluids;           // Fluid simulation
        public float FixedTimeStep;         // Fixed physics timestep
        public int MaxSubSteps;             // Maximum simulation substeps
        public float ContactBreakingThreshold; // Contact management
        public float DeactivationTime;      // Object sleep time
        public bool EnableCPUProfiling;     // Performance profiling
        public bool EnableGPUAcceleration;  // GPU-accelerated physics (if available)
    }
}