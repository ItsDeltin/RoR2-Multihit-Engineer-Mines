using System;
using System.Reflection;
using BepInEx;
using RoR2;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using EntityStates.Engi.Mine;

namespace Deltin
{
    [BepInDependency("com.bepis.r2api")]
    [BepInPlugin("com.deltin.multihitengineermines", "Multihit Engineer Mines", "1.0.0")]
    public class Multihit_Engineer_Mines : BaseUnityPlugin
    {
        public void Awake()
        {
            // Prevents mines from being destroyed after detonating.
            IL.EntityStates.Engi.Mine.Detonate.Explode += il =>
            {
                var c = new ILCursor(il);
                // Locate EntityState.Destroy(gameObject)
                c.GotoNext(
                    x => x.MatchLdarg(0),
                    x => x.MatchCallOrCallvirt<EntityStates.EntityState>("get_gameObject"),
                    x => x.MatchCallOrCallvirt<EntityStates.EntityState>("Destroy")
                );

                // Get labels pointing to the current instruction.
                var labels = c.IncomingLabels;

                // Remove instructions
                c.RemoveRange(3);

                // Update labels
                foreach (var label in labels) c.MarkLabel(label);
            };

            // Rrevent detonation of the mine until it is finished arming.
            IL.EntityStates.Engi.Mine.WaitForTarget.FixedUpdate += il =>
            {
                var c = new ILCursor(il);
                // Find the PreDetonate condition.
                c.GotoNext(
                    x => x.MatchLdarg(0),
                    x => x.MatchLdfld<WaitForTarget>("projectileTargetComponent"),
                    x => x.MatchCallvirt<RoR2.Projectile.ProjectileTargetComponent>("get_target"),
                    x => x.MatchCall<UnityEngine.Object>("op_Implicit")
                );

                // Get the if skip label
                c.Index += 4;
                var label = (ILLabel)c.Next.Operand;
                c.Index++;

                // Emit WaitForTarget.armingStateMachine
                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Call, typeof(EntityStates.Engi.Mine.BaseMineState).GetProperty("armingStateMachine", BindingFlags.Instance | BindingFlags.NonPublic).GetMethod);

                // Allow detonation only once the armingStateMachine's state is MineArmingFull
                c.EmitDelegate<Func<EntityStateMachine, bool>>(armingStateMachine => armingStateMachine.state is MineArmingFull);
                c.Emit(OpCodes.Brfalse, label);
            };

            // Make sure we do not reset undeployed mines
            IL.RoR2.CharacterMaster.AddDeployable += il =>
            {
                var c = new ILCursor(il);
                // Find the point where 'this.deployablesList[i].deployable' is added to the stack.
                c.GotoNext(
                    MoveType.After, // Place cursor after the deployable is pushed.
                    x => x.MatchLdarg(0),
                    x => x.MatchLdfld<CharacterMaster>("deployablesList"),
                    x => x.MatchLdloc(2),
                    x => x.Operand is Mono.Cecil.MethodReference methodReference && methodReference.Name == "get_Item", // List<T>.get_Item
                    x => x.MatchLdfld<DeployableInfo>("deployable")
                );

                // Duplicate 'this.deployablesList[i].deployable' so we can use it in the delegate without popping it.
                c.Emit(OpCodes.Dup);

                // Mark the mine with the 'Undeploying' component.
                // This is done so we do not reset the mine if the game wants to remove it due to reaching the mine limit.
                c.EmitDelegate<Action<Deployable>>(deployable => deployable.gameObject?.AddComponent<Undeploying>());
            };

            // Reset exploded mines.
            On.EntityStates.Engi.Mine.Detonate.Explode += (orig, detonateState) =>
            {
                orig(detonateState);

                // Undeployed: use original behaviour; destroy the mine GameObject.
                if (detonateState.GetComponent<Undeploying>())
                {
                    UnityEngine.Object.Destroy(detonateState.gameObject);
                    return;
                }

                // Stick back to the ground.
                detonateState.outer.SetNextState(new EntityStates.Engi.Mine.WaitForStick());

                // Disable the PreDetonation blinking effect.
                detonateState.transform.Find(PreDetonate.pathToPrepForExplosionChildEffect).gameObject.SetActive(false);
            };

            // Allow attaching to characters, just for fun! :)
            On.EntityStates.Engi.Mine.WaitForStick.OnEnter += (orig, waitForStick) =>
            {
                orig(waitForStick);
                waitForStick.GetComponent<RoR2.Projectile.ProjectileStickOnImpact>().ignoreCharacters = false;
            };
        }

        // Used to mark a mine as being undeployed.
        class Undeploying : UnityEngine.MonoBehaviour {}
    }
}