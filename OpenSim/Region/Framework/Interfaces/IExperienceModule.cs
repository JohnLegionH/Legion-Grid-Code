/*
 * Legion Grid — Experience System
 * IExperienceModule.cs — region-module interface for script↔experience association.
 *
 * Exposes just enough of the ExperienceModule for the caps layer
 * (OpenSim.Region.ClientStack.LindenCaps) to persist the "experience" field the
 * viewer sends in the UpdateScriptTask body — WITHOUT the caps assembly taking a
 * reference on OpenSim.Region.CoreModules. The write must go through the module
 * (not the raw service) so the module's in-memory association cache stays coherent.
 */

using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces
{
    public interface IExperienceModule
    {
        /// <summary>
        /// Associate a script item with an experience, or clear its association.
        /// <para>experienceId == UUID.Zero → remove any existing association (the viewer
        /// sends zero when "Use Experience" is unchecked).</para>
        /// <para>experienceId != UUID.Zero → associate, but ONLY if <paramref name="agentId"/>
        /// is permitted to use that experience (owner). If not permitted the association is
        /// left unchanged and the method returns false.</para>
        /// Updates both the persistent store and the module's in-memory cache.
        /// </summary>
        /// <returns>true on associate/clear; false if the agent may not use the experience.</returns>
        bool TryAssociateScriptExperience(UUID scriptItemId, UUID experienceId, UUID agentId);
    }
}
