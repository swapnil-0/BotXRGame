# BotXRGame scene report

Generated: 2026-08-28 09:38:07
Scene: GetStarted_Scene  (Assets/Scenes/GetStarted_Scene.unity)
Unity: 6000.4.2f1

## Scene roots
```
Directional Light   [Light, UniversalAdditionalLightData]
Collectibles
  Collectible_Star (1)   [BoxCollider, Pickup]
    Star_Model   [MeshFilter, MeshRenderer]
  Collectible_Star (2)   [BoxCollider, Pickup]
    Star_Model   [MeshFilter, MeshRenderer]
  Collectible_Star (3)   [BoxCollider, Pickup]
    Star_Model   [MeshFilter, MeshRenderer]
XR Origin (VR)   [XROrigin, InputActionManager, ARPlaneManager, ARRaycastManager, ARTrackedImageManager]
  Camera Offset
    Main Camera   [Camera, AudioListener, TrackedPoseDriver, UniversalAdditionalCameraData, ARCameraManager]
    Ray Interactor   [XRRayInteractor, LineRenderer, XRInteractorLineVisual, SortingGroup, TrackedPoseDriver]
AR Session   [ARSession, ARInputManager]
Canvas   [Canvas, CanvasScaler, CmdVelHUD, ROSIPConfig, TrackedDeviceGraphicRaycaster, ModeSelectMenu, ArmRosPublisher]
  IPInputPanel  [inactive]   [CanvasRenderer, Image]
    TitleText   [CanvasRenderer, TextMeshProUGUI]
    IPInputField   [CanvasRenderer, Image, TMP_InputField]
      Text Area   [RectMask2D]
    PortInputField   [CanvasRenderer, Image, TMP_InputField]
      Text Area   [RectMask2D]
    ConnectButton   [CanvasRenderer, Image, Button]
      Connect   [CanvasRenderer, TextMeshProUGUI]
    SkipButton   [CanvasRenderer, Image, Button]
      Skip (Simulation Only)   [CanvasRenderer, TextMeshProUGUI]
    IPStatusText   [CanvasRenderer, TextMeshProUGUI]
  HUDPanel   [CanvasRenderer, Image, HeadLockedHUD]
    StatusText   [CanvasRenderer, TextMeshProUGUI]
    TopicText   [CanvasRenderer, TextMeshProUGUI]
    LinearXText   [CanvasRenderer, TextMeshProUGUI]
    AngularZText   [CanvasRenderer, TextMeshProUGUI]
    EndpointText   [CanvasRenderer, TextMeshProUGUI]
    RunText   [CanvasRenderer, TextMeshProUGUI]
  ModePanel
    Background   [CanvasRenderer, Image]
    Title   [CanvasRenderer, TextMeshProUGUI]
    Help   [CanvasRenderer, TextMeshProUGUI]
    VirtualBotButton   [CanvasRenderer, Image, Button]
      Label   [CanvasRenderer, TextMeshProUGUI]
    AprilTagButton   [CanvasRenderer, Image, Button]
      Label   [CanvasRenderer, TextMeshProUGUI]
EventSystem   [EventSystem, InputSystemUIInputModule]
RosConnection   [ROSConnection]
XR Interaction Manager   [XRInteractionManager]
GameManager   [ArenaRun, ArenaPlacer, FloorSetup]
  ArenaPreview  [inactive]   [MeshFilter, MeshRenderer]
  ArenaOutline  [inactive]   [LineRenderer]
  FinishMarker  [inactive]   [MeshFilter, MeshRenderer]
ShipRoot   [GhostBot, ShipTagFollower]
  Fighter03   [MeshFilter, MeshRenderer, RobotController]
    Wing01   [MeshFilter, MeshRenderer]
      Part02   [MeshFilter, MeshRenderer]
    Part03   [MeshFilter, MeshRenderer]
    Wing01 (1)   [MeshFilter, MeshRenderer]
      Part02   [MeshFilter, MeshRenderer]
    Collider  [inactive]
      Collider01   [BoxCollider]
      Collider02   [BoxCollider]
ScoreCanvas   [Canvas, CanvasScaler, GraphicRaycaster, ScoreBoard]
  Background   [CanvasRenderer, Image]
  Headline   [CanvasRenderer, TextMeshProUGUI]
  Body   [CanvasRenderer, TextMeshProUGUI]
  Debug   [CanvasRenderer, TextMeshProUGUI]
TagStandIn   [MeshFilter, MeshRenderer, TrackedImageTagSource]
```

## UI plumbing
```
EventSystem count: 1
  EventSystem
     component: EventSystem
     component: InputSystemUIInputModule
Canvas 'Canvas'
   renderMode : WorldSpace
   worldCamera: Main Camera
   raycaster  : TrackedDeviceGraphicRaycaster
   scale      : (0.0010, 0.0010, 0.0010)
Canvas 'ScoreCanvas'
   renderMode : WorldSpace
   worldCamera: NULL
   raycaster  : GraphicRaycaster
   scale      : (0.0010, 0.0010, 0.0010)
```

## Components

### ModeSelectMenu  (1 in scene)
```
on: Canvas
   modePanel                    = ModePanel (GameObject)
   virtualBotButton             = VirtualBotButton (Button)
   aprilTagButton               = AprilTagButton (Button)
   titleText                    = Title (TextMeshProUGUI)
   helpText                     = Help (TextMeshProUGUI)
   selectVirtualAction          = Bot/Swing (InputActionReference)
   selectAprilTagAction         = Bot/Kick (InputActionReference)
   pressThreshold               = 0.5000
   ipConfig                     = Canvas (ROSIPConfig)
   skipIpConfigForVirtualBot    = False

```

### ROSIPConfig  (1 in scene)
```
on: Canvas
   ipInputPanel                 = IPInputPanel (GameObject)
   hudPanel                     = HUDPanel (GameObject)
   ipInputField                 = IPInputField (TMP_InputField)
   portInputField               = PortInputField (TMP_InputField)
   ipStatusText                 = IPStatusText (TextMeshProUGUI)
   robotController              = Fighter03 (RobotController)
   waitForModeSelection         = True

```

### HeadLockedHUD  (1 in scene)
```
on: Canvas/HUDPanel
   panel                        = HUDPanel (RectTransform)
   head                         = Main Camera (Transform)
   distance                     = 1.2000
   panelScale                   = 0.4500
   verticalOffset               = 0.2500
   deadAngle                    = 12.0000
   followTime                   = 0.3500
   keepUpright                  = True

```

### ArmRosPublisher  (1 in scene)
```
on: Canvas
   topicName                    = "/arm_command"
   publishInVirtualBotMode      = True
   swingAction                  = Bot/Swing (InputActionReference)
   kickAction                   = Bot/Kick (InputActionReference)
   pressThreshold               = 0.5000
   swingActionName              = "SWING"
   kickActionName               = "STOW"
   abortActions                 = "STOW"
   cooldownSeconds              = 1.5000
   localArm                     = NULL   <-- unassigned

```

### ShipTagFollower  (1 in scene)
```
on: ShipRoot
   tagTransform                 = TagStandIn (Transform)
   ship                         = ShipRoot (GhostBot)
   hoverHeight                  = 0.3500
   smoothTime                   = 0.1200
   followYaw                    = True
   trackingTimeout              = 0.5000

```

### ArenaPlacer  (1 in scene)
```
on: GameManager
   raycastManager               = XR Origin (VR) (ARRaycastManager)
   rayOrigin                    = Ray Interactor (Transform)
   placeAction                  = Bot/Place (InputActionReference)
   pressThreshold               = 0.5000
   arenaSizeOptionsFeet         = 5 items
   defaultSizeIndex             = 0
   arenaSize                    = 1.8288
   hoverHeight                  = 0.0400
   tornadoRadiusFraction        = 0.3000
   tornadoPeriodFraction        = 0.9000
   samplesPerSide               = 5
   useMeshObstacles             = True
   obstacleMask                 = (LayerMask)
   ship                         = ShipRoot (Transform)
   previewSurface               = ArenaPreview (MeshRenderer)
   previewOutline               = ArenaOutline (LineRenderer)
   finishMarker                 = FinishMarker (Transform)
   tornadoPrefab                = Tornado (GameObject)
   validColour                  = RGBA(0.200, 0.500, 1.000, 0.350)
   invalidColour                = RGBA(1.000, 0.250, 0.200, 0.350)
   placedColour                 = RGBA(0.200, 0.900, 0.350, 0.300)
   cupCount                     = 4
   cupHeight                    = 0.1000
   cupCollectRadius             = 0.1300
   tornadoCount                 = 2
   twinTornadoRadiusFraction    = 0.1600
   tornadoPatrolFraction        = 0.2800
   cupMaterial                  = CupGreen (Material)
   scaleDensityWithArena        = True
   shipStartClearance           = 0.4500
   showCenterMarker             = True
   lockShipVisual               = True
   tornadoSuck                  = 0.9500
   tornadoSwirl                 = 0.2500

```

### ArenaRun  (1 in scene)
```
on: GameManager
   ship                         = ShipRoot (GhostBot)
   hudText                      = RunText (TextMeshProUGUI)
   finishRadius                 = 0.1000
   requireAllCups               = True
   clampToArena                 = True
   targetCrossingSeconds        = 9.0000
   speedMultiplier              = 1.5000
   turnSpeedMultiplier          = 1.5000
   clampMargin                  = 0.1500
   finishEffect                 = NULL   <-- unassigned
   finishMarkerVisual           = FinishMarker (Transform)
   calmTornadoOnFinish          = True
   audioSource                  = NULL   <-- unassigned
   finishClip                   = NULL   <-- unassigned
   scoreBoard                   = ScoreCanvas (ScoreBoard)
   showTelemetry                = True
   tornado                      = NULL   <-- unassigned
   targetTurnSeconds            = 2.5000
   overrideShipSpeed            = True
   captureHoldSeconds           = 3.0000
   captureSpinDegreesPerSecond  = 540.0000
   capturePenaltySeconds        = 3.0000
   respawnAtStart               = True

```

### ScoreBoard  (1 in scene)
```
on: ScoreCanvas
   boardRoot                    = ScoreCanvas (RectTransform)
   headlineText                 = Headline (TextMeshProUGUI)
   bodyText                     = Body (TextMeshProUGUI)
   debugText                    = Debug (TextMeshProUGUI)
   distanceBehindFinish         = 0.3500
   heightAboveFloor             = 0.2800
   tiltDegrees                  = 25.0000
   showDebug                    = True
   hideUntilPlaced              = True
   cupRadiusForDisplay          = 0.2800

```

### GhostBot  (1 in scene)
```
on: ShipRoot
   moveAction                   = Bot/Move (InputActionReference)
   linearSpeed                  = 0.6000
   angularSpeed                 = 2.0000
   deadzone                     = 0.1500
   accelerationTime             = 0.4000
   turnAccelerationTime         = 0.2500
   playAreaCenter               = NULL   <-- unassigned
   playAreaSize                 = (2.440, 2.440)
   acceptExternalForces         = True
   angularDeadband              = 0.0200
   linearDeadband               = 0.0100
   axisDeadzone                 = 0.2500

```

### ShipVisualLock  (0 in scene)
_none_

### CenterMarker  (0 in scene)
_none_

### RobotController  (1 in scene)
```
on: ShipRoot/Fighter03
   rosIP                        = "192.168.1.100"
   rosPort                      = 10000
   topicName                    = "/cmd_vel"
   publishRate                  = 10.0000
   linearSpeed                  = 1.0000
   angularSpeed                 = 1.5000
   moveAction                   = Bot/Move (InputActionReference)
   triggerAction                = XRI Right Interaction/Activate Value (InputActionReference)
   moveInSimulation             = False

```

### ArmController  (0 in scene)
_none_

### Tornado  (0 in scene)
_none_

### CollectibleCup  (0 in scene)
_none_

### FloorSetup  (1 in scene)
```
on: GameManager
   planeManager                 = XR Origin (VR) (ARPlaneManager)
   detectionMode                = Horizontal
   permissionTimeout            = 20.0000

```
## Input action assets
```
Assets/Samples/XR Interaction Toolkit/3.5.1/Starter Assets/XRI Default Input Actions.inputactions
Assets/Samples/XR Interaction Toolkit/3.5.1/XR Device Simulator/XR Device Controller Controls.inputactions
Assets/Samples/XR Interaction Toolkit/3.5.1/XR Device Simulator/XR Device Hand Controls.inputactions
Assets/Samples/XR Interaction Toolkit/3.5.1/XR Device Simulator/XR Device Simulator Controls.inputactions
Assets/SourceFiles/InputSystem/BotXRGameControls.inputactions
  map: Bot
    Move (Vector2)
       <- <XRController>{RightHand}/thumbstick
    Place (Button)
       <- <XRController>{RightHand}/trigger
    Swing (Button)
       <- <XRController>{RightHand}/primaryButton
    Kick (Button)
       <- <XRController>{RightHand}/secondaryButton
Assets/SourceFiles/InputSystem/InputSystem_Actions.inputactions
Assets/SourceFiles/InputSystem/StarterAssets.inputactions
Packages/com.unity.xr.arfoundation/Assets/InputActions/XR Simulation Input Actions.inputactions
Packages/com.unity.cinemachine/Runtime/Input/CinemachineDefaultInputActions.inputactions
Packages/com.unity.inputsystem/InputSystem/Plugins/PlayerInput/DefaultInputActions.inputactions
```

