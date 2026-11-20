using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace XRStudy.Task1
{
    /// <summary>
    /// Select/hover filter on every mug (spec FR-05): only the designated hand's interactors (hand or controller)
    /// may grab; poke interactors never grab. Far grabbing is prevented separately by switching far casting off
    /// during pick & place trials. The same filter is on every mug, so all mugs have identical permissions.
    /// </summary>
    public class DesignatedHandSelectFilter : MonoBehaviour, IXRSelectFilter, IXRHoverFilter
    {
        public StudyHand designatedHand = StudyHand.Right;

        public bool canProcess => isActiveAndEnabled;

        public static bool Matches(IXRInteractor interactor, StudyHand hand)
        {
            if (interactor == null || interactor is XRPokeInteractor) return false;
            var want = hand == StudyHand.Right ? InteractorHandedness.Right : InteractorHandedness.Left;
            return interactor.handedness == want;
        }

        public bool Process(IXRSelectInteractor interactor, IXRSelectInteractable interactable) => Matches(interactor, designatedHand);
        public bool Process(IXRHoverInteractor interactor, IXRHoverInteractable interactable) => Matches(interactor, designatedHand);
    }
}
