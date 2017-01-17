﻿using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Assertions;
using System;
using System.Collections.Generic;
using Leap.Unity.RuntimeGizmos;
using Leap.Unity.Attributes;

namespace Leap.Unity.Interaction {

  /// <summary>
  /// InteractionManager is the core behaviour that manages the IInteractionBehaviours in the scene.
  /// This class allows IInteractionBehaviours to register with it and provides all of the callbacks
  /// needed for operation.  This class also takes care of all bookkeeping to keep track of the objects,
  /// hands, and the internal state of the interaction plugin.
  /// </summary>
  ///
  /// <remarks>
  /// InteractionManager has the following features:
  ///    - Allows instances of IInteractionBehaviour to register or unregister with it.
  ///    - Registered instances stay registered even if this behaviour is disabled.
  ///    - Dispatches events to the interaction plugin and uses the returned data to drive the registered
  ///      behaviours.  Takes care of all bookkeeping needed to maintain the internal state.
  ///    - Supports the concept of 'suspension', where an untracked hand is still allowed to be considered
  ///      grasping an object.  This is to allow an object to not fall when a hand becomes untracked for
  ///      a small amount of time.  This helps with actions such as throwing.
  ///    - Multiple instances of InteractionManager are ALLOWED!  This allows you to have different simulation
  ///      settings and control for different groups of objects.
  ///
  /// InteractionManager has the following requirements:
  ///    - The DataSubfolder property must point to a valid subfolder in the StreamingAssets data folder.
  ///      The subfolder must contain a valid ldat file names IE.
  /// </remarks>
  public partial class InteractionManager : MonoBehaviour {
    #region SERIALIZED FIELDS
    [AutoFind]
    [SerializeField]
    protected LeapProvider _leapProvider;

    [AutoFind]
    [SerializeField]
    protected HandPool _handPool;

    [Tooltip("The streaming asset subpath of the ldat engine.")]
    [SerializeField]
    protected string _ldatPath = "InteractionEngine/IE.ldat";

    [Header("Interaction Settings")]
    [Tooltip("The default Interaction Material to use for Interaction Behaviours if none is specified, or for Interaction Behaviours created via scripting.")]
    [SerializeField]
    protected InteractionMaterial _defaultInteractionMaterial;

    [Tooltip("The name of the model group of the Hand Pool containing the brush hands.")]
    [SerializeField]
    protected string _brushGroupName = "BrushHands";

    [Tooltip("Allow the Interaction Engine to modify object velocities when pushing.")]
    [SerializeField]
    protected bool _contactEnabled = true;

    [Tooltip("Allow the Interaction plugin to modify object positions by grasping.")]
    [SerializeField]
    protected bool _graspingEnabled = true;

    [Tooltip("Objects within this radius of a hand will be considered for interaction.")]
    [SerializeField]
    protected float _activationRadius = 0.15f;

    [Tooltip("How many objects away from the hand are still considered for interaction.")]
    [SerializeField]
    protected int _maxActivationDepth = 3;

    [Header("Layer Settings")]
    [Tooltip("Whether or not to create the layers used for interaction when the scene runs.")]
    [SerializeField]
    protected bool _autoGenerateLayers = false;

    [Tooltip("Layer to use for auto-generation.  The generated interaction layers will have the same collision settings as this layer.")]
    [SerializeField]
    protected SingleLayer _templateLayer = 0;

    [Tooltip("Default layer that interaction objects")]
    [SerializeField]
    protected SingleLayer _interactionLayer = 0;

    [Tooltip("Default layer that interaction objects when they become grasped.")]
    [SerializeField]
    protected SingleLayer _interactionNoClipLayer = 0;

    [Tooltip("Layer that interaction brushes will be on normally.")]
    [SerializeField]
    protected SingleLayer _brushLayer = 0;

    [Header("Debug")]
    [Tooltip("Automatically validate integrity of simulation state each frame.  Can cause slowdown, but is always compiled out for release builds.")]
    [SerializeField]
    protected bool _automaticValidation = false;

    [Tooltip("Shows the debug visualization coming from the internal Interaction plugin.")]
    [SerializeField]
    protected bool _showDebugLines = false;

    [Tooltip("Shows the debug messages coming from the internal Interaction plugin.")]
    [SerializeField]
    protected bool _showDebugOutput = false;

    [Tooltip("Will display the debug messages if assigned.")]
    [SerializeField]
    protected Text _debugTextView;
    #endregion

    #region INTERNAL FIELDS
    private const float UNSCALED_RECOMMENDED_CONTACT_OFFSET_MAXIMUM = 0.001f; //One millimeter
    public float RecommendedContactOffsetMaximum {
      get {
        return UNSCALED_RECOMMENDED_CONTACT_OFFSET_MAXIMUM * SimulationScale;
      }
    }

    private bool _hasSceneBeenCreated = false;
    private bool _enableGraspingLast = false;

    protected ActivityManager _activityManager = new ActivityManager();
    protected Dictionary<int, InteractionHand> _idToInteractionHand = new Dictionary<int, InteractionHand>();
    protected List<IInteractionBehaviour> _graspedBehaviours = new List<IInteractionBehaviour>();

    private float _cachedSimulationScale = -1;
    //A temp list that is recycled.  Used to remove items from _handIdToIeHand.
    private List<int> _handIdsToRemove = new List<int>();
    //A temp list that is recycled.  Used as the argument to OnHandsHold.
    private List<Hand> _holdingHands = new List<Hand>();
    //A temp list that is recycled.  Used to recieve debug logs from InteractionC.
    private List<string> _debugOutput = new List<string>();
    #endregion

    #region PUBLIC METHODS
    public Action OnGraphicalUpdate;
    public Action OnPrePhysicalUpdate;
    public Action OnPostPhysicalUpdate;

    public float SimulationScale {
      get {
#if UNITY_EDITOR
        if (Application.isPlaying) {
          return _cachedSimulationScale;
        } else {
          if (_leapProvider != null) {
            return _leapProvider.transform.lossyScale.x;
          } else {
            return 1;
          }
        }
#else
        return _cachedSimulationScale;
#endif
      }
    }

    /// <summary>
    /// Returns true if any InteractionObject is currently being grasped by at least one Hand.
    /// </summary>
    public bool IsAnyObjectGrasped {
      get {
        return _graspedBehaviours.Count != 0;
      }
    }

    /// <summary>
    /// Returns a collection of InteractionBehaviours that are currently registered with this manager.
    /// </summary>
    public IEnumerable<IInteractionBehaviour> RegisteredObjects {
      get {
        return _activityManager.RegisteredObjects;
      }
    }

    /// <summary>
    /// Returns a collection of InteractionBehaviours that are currently being grasped by
    /// at least one hand.
    /// </summary>
    public ReadonlyList<IInteractionBehaviour> GraspedObjects {
      get {
        return _graspedBehaviours;
      }
    }

    /// <summary>
    /// Gets or sets the default InteractionMaterial used when InteractionBehaviours are spawned without a material explicitly assigned.
    /// </summary>
    public InteractionMaterial DefaultInteractionMaterial {
      get {
        return _defaultInteractionMaterial;
      }
      set {
        _defaultInteractionMaterial = value;
      }
    }

    /// <summary>
    /// Gets or sets whether or not the Interaction Engine can modify object velocities when pushing.
    /// </summary>
    public bool ContactEnabled {
      get {
        return _contactEnabled;
      }
      set {
        if (_contactEnabled != value) {
          _contactEnabled = value;

          if (_handPool != null) {
            if (_contactEnabled) {
              _handPool.EnableGroup(_brushGroupName);
            } else {
              _handPool.DisableGroup(_brushGroupName);
            }
          }

          UpdateSceneInfo();
        }
      }
    }

    /// <summary>
    /// Gets or sets whether or not the Interaction Engine can modify object positions by grasping.
    /// </summary>
    public bool GraspingEnabled {
      get {
        return _graspingEnabled;
      }
      set {
        if (_graspingEnabled != value) {
          _graspingEnabled = value;
          UpdateSceneInfo();
        }
      }
    }

    /// <summary>
    /// Gets the layer that interaction objects should be on by default.
    /// </summary>
    public int InteractionLayer {
      get {
        return _interactionLayer;
      }
      set {
        _interactionLayer = value;
      }
    }

    /// <summary>
    /// Gets the layer that interaction objects should be on when they become grasped.
    /// </summary>
    public int InteractionNoClipLayer {
      get {
        return _interactionNoClipLayer;
      }
      set {
        _interactionNoClipLayer = value;
      }
    }

    /// <summary>
    /// Gets the layer that interaction brushes should be on normally.
    /// </summary>
    public int InteractionBrushLayer {
      get {
        return _brushLayer;
      }
      set {
        _brushLayer = value;
      }
    }

    /// <summary>
    /// Gets or sets the max activation depth.
    /// </summary>
    public int MaxActivationDepth {
      get {
        return _maxActivationDepth;
      }
      set {
        _maxActivationDepth = value;
        _activityManager.MaxDepth = value;
      }
    }

    /// <summary>
    /// Enables the display of proximity information from the library.
    /// </summary>
    public bool ShowDebugLines {
      get {
        return _showDebugLines;
      }
      set {
        _showDebugLines = value;
      }
    }

    /// <summary>
    /// Enables the display of debug text from the library.
    /// </summary>
    public bool ShowDebugOutput {
      get {
        return _showDebugOutput;
      }
      set {
        _showDebugOutput = value;
      }
    }

    /// Force an update of the internal scene info.  This should be called if gravity has changed.
    /// </summary>
    public void UpdateSceneInfo() {
      if (!_hasSceneBeenCreated) {
        return; // UpdateSceneInfo is a side effect of a lot of changes.
      }

      _enableGraspingLast = _graspingEnabled;

      _cachedSimulationScale = _leapProvider.transform.lossyScale.x;
      _activityManager.OverlapRadius = _activationRadius * _cachedSimulationScale;
    }

    /// <summary>
    /// Tries to find an InteractionObject that is currently being grasped by a Hand with
    /// the given ID.
    /// </summary>
    public bool TryGetGraspedObject(int handId, out IInteractionBehaviour graspedObject) {
      for (int i = 0; i < _graspedBehaviours.Count; i++) {
        var iObj = _graspedBehaviours[i];
        if (iObj.IsBeingGraspedByHand(handId)) {
          graspedObject = iObj;
          return true;
        }
      }

      graspedObject = null;
      return false;
    }

    /// <summary>
    /// Forces the given object to be released by any hands currently holding it.  Will return true
    /// only if there was at least one hand holding the object.
    /// </summary>
    public bool ReleaseObject(IInteractionBehaviour graspedObject) {
      if (!_graspedBehaviours.Remove(graspedObject)) {
        return false;
      }

      for (var it = _idToInteractionHand.GetEnumerator(); it.MoveNext();) {
        var interactionHand = it.Current.Value;
        if (interactionHand.graspedObject == graspedObject) {
          if (interactionHand.isUntracked) {
            interactionHand.MarkTimeout();
          } else {
            if (_graspingEnabled) {
            }
            interactionHand.ReleaseObject();
          }
        }
      }

      return true;
    }

    /// <summary>
    /// Forces a hand with the given id to release an object if it is holding it.  Will return true
    /// only if a hand with the given id releases an object it was holding.
    /// </summary>
    public bool ReleaseHand(int handId) {
      InteractionHand interactionHand;
      if (!_idToInteractionHand.TryGetValue(handId, out interactionHand)) {
        return false;
      }

      if (interactionHand.graspedObject == null) {
        return false;
      }

      if (interactionHand.graspedObject.GraspingHandCount == 1) {
        _graspedBehaviours.Remove(interactionHand.graspedObject);
      }

      interactionHand.ReleaseObject();
      return true;
    }

    /// <summary>
    /// Forces a hand to grasp the given interaction behaviour.  The grasp will only be terminated when
    /// the hand either times out or the user calls ReleaseHand.
    /// </summary>
    /// <param name="hand"></param>
    public void GraspWithHand(Hand hand, IInteractionBehaviour interactionBehaviour) {
      if (!_activityManager.IsRegistered(interactionBehaviour)) {
        throw new InvalidOperationException("Cannot grasp " + interactionBehaviour + " because it is not registered with this manager.");
      }

      InteractionHand interactionHand;
      if (!_idToInteractionHand.TryGetValue(hand.Id, out interactionHand)) {
        throw new InvalidOperationException("Hand with id " + hand.Id + " is not registered with this manager.");
      }

      if (interactionHand.graspedObject != null) {
        throw new InvalidOperationException("Cannot grasp with hand " + hand.Id + " because that hand is already grasping " + interactionHand.graspedObject);
      }

      //Ensure behaviour is active already
      _activityManager.Activate(interactionBehaviour);

      if (!interactionBehaviour.IsBeingGrasped) {
        _graspedBehaviours.Add(interactionBehaviour);
      }

      interactionHand.GraspObject(interactionBehaviour, isUserGrasp: true);
    }

    /// <summary>
    /// Registers an InteractionObject with this manager, which automatically adds the objects
    /// representation into the internal interaction scene.  If the manager is disabled,
    /// the registration will still succeed and the object will be added to the internal scene
    /// when the manager is next enabled.
    ///
    /// Trying to register a behaviour that is already registered is safe and is a no-op.
    /// </summary>
    public void RegisterInteractionBehaviour(IInteractionBehaviour interactionBehaviour) {
      _activityManager.Register(interactionBehaviour);
    }

    /// <summary>
    /// Unregisters an InteractionObject from this manager.  This removes it from the internal
    /// scene and prevents any further interaction.
    ///
    /// Trying to unregister a behaviour that is not registered is safe and is a no-op.
    /// </summary>
    public void UnregisterInteractionBehaviour(IInteractionBehaviour interactionBehaviour) {
      if (_graspedBehaviours.Remove(interactionBehaviour)) {
        for (var it = _idToInteractionHand.GetEnumerator(); it.MoveNext();) {
          var interactionHand = it.Current.Value;
          if (interactionHand.graspedObject == interactionBehaviour) {
            try {
              if (interactionHand.isUntracked) {
                interactionHand.MarkTimeout();
              } else {
                interactionHand.ReleaseObject();
              }
            } catch (Exception e) {
              //Only log to console
              //We want to continue so we can destroy the shape and dispatch OnUnregister
              Debug.LogException(e);
            }
            break;
          }
        }
      }

      _activityManager.Unregister(interactionBehaviour);
    }

    public void EnsureActive(IInteractionBehaviour interactionBehaviour) {
      if (!_activityManager.IsActive(interactionBehaviour)) {
        _activityManager.Activate(interactionBehaviour);
      }
    }

    #endregion

    #region UNITY CALLBACKS
    protected virtual void Reset() {
      if (_leapProvider == null) {
        _leapProvider = FindObjectOfType<LeapProvider>();
      }
    }

    protected virtual void OnValidate() {
      if (Application.isPlaying && _hasSceneBeenCreated) {
        //Allow scene info to be updated while the scene is playing
        UpdateSceneInfo();
      }

      if (!Application.isPlaying && _autoGenerateLayers) {
        autoGenerateLayers();
      }

      _activationRadius = Mathf.Max(0, _activationRadius);
      _maxActivationDepth = Mathf.Max(1, _maxActivationDepth);

      if (_activityManager != null) {
        _activityManager.OverlapRadius = _activationRadius;
        _activityManager.MaxDepth = _maxActivationDepth;
      }
    }

    protected virtual void Awake() {
      if (_autoGenerateLayers) {
        autoGenerateLayers();
        autoSetupCollisionLayers();
      }
    }

    protected virtual void OnEnable() {
      if (_leapProvider == null) {
        enabled = false;
        Debug.LogError("Could not enable Interaction Manager because no Leap Provider was specified.");
        return;
      }

      _cachedSimulationScale = _leapProvider.transform.lossyScale.x;

      _activityManager.BrushLayer = InteractionBrushLayer;
      _activityManager.OverlapRadius = _activationRadius * _cachedSimulationScale;
      _activityManager.MaxDepth = _maxActivationDepth;

      if (_handPool != null) {
        if (_contactEnabled) {
          _handPool.EnableGroup(_brushGroupName);
        } else {
          _handPool.DisableGroup(_brushGroupName);
        }
      }
    }

    protected virtual void OnDisable() {
      for (int i = _graspedBehaviours.Count; i-- != 0;) {
        ReleaseObject(_graspedBehaviours[i]);
      }

      _activityManager.UnregisterMisbehavingObjects();

      _idToInteractionHand.Clear();
      _graspedBehaviours.Clear();

      _activityManager.DeactivateAll();
    }

    protected virtual void FixedUpdate() {
      Frame frame = _leapProvider.CurrentFixedFrame;

      if (OnPrePhysicalUpdate != null) {
        OnPrePhysicalUpdate();
      }

      simulateFrame(frame);

      if (OnPostPhysicalUpdate != null) {
        OnPostPhysicalUpdate();
      }
    }

    protected virtual void LateUpdate() {
      Frame frame = _leapProvider.CurrentFrame;

      dispatchOnHandsHoldingAll(frame, isPhysics: false);

      _activityManager.UnregisterMisbehavingObjects();

      if (OnGraphicalUpdate != null) {
        OnGraphicalUpdate();
      }

      if (_showDebugOutput && _debugTextView != null) {
        string text = "";
        for (int i = 0; i < _debugOutput.Count; i++) {
          text += _debugOutput[i];
          if (i != _debugOutput.Count - 1) {
            text += "\n";
          }
        }
        _debugTextView.text = text;
      }

      if (_automaticValidation) {
        Validate();
      }
    }

    protected virtual void OnGUI() {
      if (_showDebugOutput) {
        for (int i = 0; i < _debugOutput.Count; i++) {
          GUILayout.Label(_debugOutput[i]);
        }
      }
    }
    #endregion

    #region INTERNAL METHODS

    protected void autoGenerateLayers() {
      _interactionLayer = -1;
      _interactionNoClipLayer = -1;
      _brushLayer = -1;
      for (int i = 8; i < 32; i++) {
        string layerName = LayerMask.LayerToName(i);
        if (string.IsNullOrEmpty(layerName)) {
          if (_interactionLayer == -1) {
            _interactionLayer = i;
          } else if (_interactionNoClipLayer == -1) {
            _interactionNoClipLayer = i;
          } else if (_brushLayer == -1) {
            _brushLayer = i;
            break;
          }
        }
      }

      if (_interactionLayer == -1 || _interactionNoClipLayer == -1 || _brushLayer == -1) {
        if (Application.isPlaying) {
          enabled = false;
        }
        Debug.LogError("InteractionManager Could not find enough free layers for auto-setup, manual setup required.");
        _autoGenerateLayers = false;
        return;
      }
    }

    private void autoSetupCollisionLayers() {
      for (int i = 0; i < 32; i++) {
        // Copy ignore settings from template layer
        bool shouldIgnore = Physics.GetIgnoreLayerCollision(_templateLayer, i);
        Physics.IgnoreLayerCollision(_interactionLayer, i, shouldIgnore);
        Physics.IgnoreLayerCollision(_interactionNoClipLayer, i, shouldIgnore);

        // Set brush layer to collide with nothing
        Physics.IgnoreLayerCollision(_brushLayer, i, true);
      }

      //After copy and set we enable the interaction between the brushes and interaction objects
      Physics.IgnoreLayerCollision(_brushLayer, _interactionLayer, false);
    }

    protected virtual void simulateFrame(Frame frame) {
      _activityManager.UpdateState(frame);

      var active = _activityManager.ActiveBehaviours;

      for (int i = 0; i < active.Count; i++) {
        active[i].NotifyPreSolve();
      }

      dispatchOnHandsHoldingAll(frame, isPhysics: true);

      //Simulation went here

      updateInteractionStateChanges(frame);


      for (int i = 0; i < active.Count; i++) {
        active[i].NotifyPostSolve();
      }
    }

    protected virtual void dispatchOnHandsHoldingAll(Frame frame, bool isPhysics) {
      var hands = frame.Hands;
      //Loop through the currently grasped objects to dispatch their OnHandsHold callback
      for (int i = 0; i < _graspedBehaviours.Count; i++) {
        dispatchOnHandsHolding(hands, _graspedBehaviours[i], isPhysics);
      }
    }

    protected virtual void dispatchOnHandsHolding(List<Hand> hands, IInteractionBehaviour interactionBehaviour, bool isPhysics) {
      for (int j = 0; j < hands.Count; j++) {
        var hand = hands[j];
        InteractionHand interactionHand;
        if (_idToInteractionHand.TryGetValue(hand.Id, out interactionHand)) {
          if (interactionHand.graspedObject == interactionBehaviour) {
            _holdingHands.Add(hand);
          }
        }
      }

      try {
        if (isPhysics) {
          interactionBehaviour.NotifyHandsHoldPhysics(_holdingHands);
        } else {
          interactionBehaviour.NotifyHandsHoldGraphics(_holdingHands);
        }
      } catch (Exception e) {
        _activityManager.NotifyMisbehaving(interactionBehaviour);
        Debug.LogException(e);
      }

      _holdingHands.Clear();
    }

    protected virtual void updateInteractionStateChanges(Frame frame) {
      var hands = frame.Hands;

      //First loop through all the hands and get their classifications from the engine
      for (int i = 0; i < hands.Count; i++) {
        Hand hand = hands[i];

        //Get the InteractionHand associated with this hand id
        InteractionHand interactionHand;
        if (!_idToInteractionHand.TryGetValue(hand.Id, out interactionHand)) {

          //First we see if there is an untracked interactionHand that can be re-connected using this one
          InteractionHand untrackedInteractionHand = null;
          for (var it = _idToInteractionHand.GetEnumerator(); it.MoveNext();) {
            var pair = it.Current;
            //If the old ieHand is untracked, and the handedness matches, we re-connect it
            if (pair.Value.isUntracked && pair.Value.hand.IsLeft == hand.IsLeft) {
              untrackedInteractionHand = pair.Value;
              break;
            }
          }

          if (untrackedInteractionHand != null) {
            //If we found an untrackedIeHand, use it!
            interactionHand = untrackedInteractionHand;
            //Remove the old id from the mapping
            _idToInteractionHand.Remove(untrackedInteractionHand.hand.Id);
            _idToInteractionHand[hand.Id] = interactionHand;
            interactionHand.hand.Id = hand.Id;

            try {
              //This also dispatched InteractionObject.OnHandRegainedTracking()
              interactionHand.RegainTracking(hand);

              if (interactionHand.graspedObject == null) {
                continue;
              }

              // NotifyHandRegainedTracking() did not throw, continue on to NotifyHandsHoldPhysics().
              dispatchOnHandsHolding(hands, interactionHand.graspedObject, isPhysics: true);
            } catch (Exception e) {
              _activityManager.NotifyMisbehaving(interactionHand.graspedObject);
              Debug.LogException(e);
              continue;
            }

            //Override the existing classification to force the hand to grab the old object
            //HANDLE THIS IF NECESSARY?
          } else {
            //Otherwise just create a new one
            interactionHand = new InteractionHand(hand);
            _idToInteractionHand[hand.Id] = interactionHand;
          }
        }

        interactionHand.UpdateHand(hand);

        //Loop through all ieHands to check for timeouts and loss of tracking
        for (var it = _idToInteractionHand.GetEnumerator(); it.MoveNext();) {
          var pair = it.Current;
          var id = pair.Key;
          var ieHand = pair.Value;

          float handAge = Time.unscaledTime - ieHand.lastTimeUpdated;
          //Check to see if the hand is at least 1 frame old
          //We assume it has become untracked if this is the case
          if (handAge > 0) {
            //If the hand isn't grasping anything, just remove it
            if (ieHand.graspedObject == null) {
              _handIdsToRemove.Add(id);
              continue;
            }

            //If is isn't already marked as untracked, mark it as untracked
            if (!ieHand.isUntracked) {
              try {
                //This also dispatches InteractionObject.OnHandLostTracking()
                ieHand.MarkUntracked();
              } catch (Exception e) {
                _activityManager.NotifyMisbehaving(ieHand.graspedObject);
                Debug.LogException(e);
              }
            }

            //If the age is longer than the timeout, we also remove it from the list
            if (handAge >= ieHand.maxSuspensionTime) {
              _handIdsToRemove.Add(id);

              try {
                if (ieHand.graspedObject.GraspingHandCount == 1) {
                  _graspedBehaviours.Remove(ieHand.graspedObject);
                }

                //This also dispatched InteractionObject.OnHandTimeout()
                ieHand.MarkTimeout();
              } catch (Exception e) {
                _activityManager.NotifyMisbehaving(ieHand.graspedObject);
                Debug.LogException(e);
              }
            }
          }
        }
      }

      //Loop through the stale ids and remove them from the map
      for (int i = 0; i < _handIdsToRemove.Count; i++) {
        _idToInteractionHand.Remove(_handIdsToRemove[i]);
      }
      _handIdsToRemove.Clear();
    }

    //A persistant structure for storing useful data about a hand as it interacts with objects
    //TODO: Investigate pooling?
    protected partial class InteractionHand {
      public Hand hand { get; protected set; }
      public float lastTimeUpdated { get; protected set; }
      public float maxSuspensionTime { get; protected set; }
      public IInteractionBehaviour graspedObject { get; protected set; }
      public bool isUntracked { get; protected set; }
      public bool isUserGrasp { get; protected set; }

      public InteractionHand(Hand hand) {
        this.hand = new Hand().CopyFrom(hand);
        lastTimeUpdated = Time.unscaledTime;
        graspedObject = null;
      }

      public void UpdateHand(Hand hand) {
        this.hand.CopyFrom(hand);
        lastTimeUpdated = Time.unscaledTime;
      }

      public void GraspObject(IInteractionBehaviour obj, bool isUserGrasp) {
        this.isUserGrasp = isUserGrasp;
        graspedObject = obj;
        graspedObject.NotifyHandGrasped(hand);
      }

      public void ReleaseObject() {
        graspedObject.NotifyHandReleased(hand);
        graspedObject = null;
        isUntracked = false;
        isUserGrasp = false;
      }

      public void MarkUntracked() {
        isUntracked = true;
        float outTime;
        graspedObject.NotifyHandLostTracking(hand, out outTime);
        maxSuspensionTime = outTime;
      }

      public void MarkTimeout() {
        graspedObject.NotifyHandTimeout(hand);
        graspedObject = null;
        isUntracked = true;
        isUserGrasp = false;
        hand = null;
      }

      public void RegainTracking(Hand newHand) {
        int oldId = hand.Id;
        UpdateHand(newHand);

        isUntracked = false;
        graspedObject.NotifyHandRegainedTracking(newHand, oldId);
      }
    }
    #endregion
  }
}
