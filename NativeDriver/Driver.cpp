#include <windows.h>
#include <openvr_driver.h>
#include <MinHook.h>
#include <cmath>
#include <cstring>
#include <mutex>
#include <cstdio>
#include <filesystem>

namespace {
using Q = vr::HmdQuaternion_t;
struct V { double x{}, y{}, z{}; };
V operator+(V a,V b){return {a.x+b.x,a.y+b.y,a.z+b.z};}
V operator-(V a,V b){return {a.x-b.x,a.y-b.y,a.z-b.z};}
Q mul(Q a,Q b){return {a.w*b.w-a.x*b.x-a.y*b.y-a.z*b.z,
 a.w*b.x+a.x*b.w+a.y*b.z-a.z*b.y,a.w*b.y-a.x*b.z+a.y*b.w+a.z*b.x,
 a.w*b.z+a.x*b.y-a.y*b.x+a.z*b.w};}
Q inverse(Q a){return {a.w,-a.x,-a.y,-a.z};}
Q unit(Q a){double n=std::sqrt(a.w*a.w+a.x*a.x+a.y*a.y+a.z*a.z);
 return n>0.000001 ? Q{a.w/n,a.x/n,a.y/n,a.z/n} : Q{1,0,0,0};}
V rotate(Q q,V v){Q p=mul(mul(q,{0,v.x,v.y,v.z}),inverse(q));return {p.x,p.y,p.z};}
V readVec(const double* v){return {v[0],v[1],v[2]};}
struct Transform {Q rotation{1,0,0,0}; V translation{};};
struct Channel {volatile LONG server, command, heartbeat, active, displayFrame, accelerationSteps, spinResistance, bounce; float floorX,floorY,floorZ,floorOffset; LONG gravity; float bounceOffset,ceiling; volatile LONG poseSequence; float poseOffset[7]; LONG wallCount; float walls[4][4]; volatile LONG requiresDisplayCorrection; volatile LONG pivotDevice;};
HANDLE mapping{};
Channel* channel{};
std::mutex poseLock;
Transform applied, beforeHold;
Q initial{};
V headset{};
Q headOrientation{1,0,0,0};
DWORD headTime{};
int heldDevice=-1;
bool suppress=false;
V previousHand{}, rotationPivot{}, glideVelocity{}, angularVelocity{};
V rawPositions[64]{};
DWORD rawPositionTimes[64]{};
Q sampledHandRotation{};
V scaled(V v,double factor){return {v.x*factor,v.y*factor,v.z*factor};}
V angularStep(Q delta){
 if(delta.w<0)delta={-delta.w,-delta.x,-delta.y,-delta.z};
 double length=std::hypot(std::hypot(delta.x,delta.y),delta.z);
 return length>1e-12 ? scaled({delta.x,delta.y,delta.z},2*std::atan2(length,delta.w)/length) : V{};
}
DWORD handSample{}, glideTime{};
double gravitySpeed{};
double dot(V a,V b){return a.x*b.x+a.y*b.y+a.z*b.z;}
void coast(Transform& transform,V& velocity,int resistance,double seconds){
 if(resistance>=100 || !std::isfinite(velocity.x) || !std::isfinite(velocity.y) || !std::isfinite(velocity.z)){velocity={};return;}
 double damping=-std::log1p(-resistance/100.0);
 double remaining=std::exp(-damping*seconds);
 double distance=damping>0 ? -std::expm1(-damping*seconds)/damping : seconds;
 transform.translation=transform.translation+V{velocity.x*distance,velocity.y*distance,velocity.z*distance};
 velocity={velocity.x*remaining,velocity.y*remaining,velocity.z*remaining};
}
void coastRotation(Transform& transform,V pivot,V& spin,int resistance,double seconds){
 Transform integrated{};
 coast(integrated,spin,resistance,seconds);
 V step=integrated.translation;
 double angle=std::hypot(std::hypot(step.x,step.y),step.z);
 if(angle<=1e-12 || !std::isfinite(angle))return;
 V axis=scaled(step,std::sin(angle*.5)/angle);
 Q turn{std::cos(angle*.5),axis.x,axis.y,axis.z};
 V offset=rotate(transform.rotation,pivot);
 transform.rotation=unit(mul(turn,transform.rotation));
 transform.translation=transform.translation+(offset-rotate(transform.rotation,pivot));
}
using PoseFn=void(*)(void*,uint32_t,const vr::DriverPose_t&,uint32_t);
PoseFn original[2]{};
void* targets[2]{};

struct PoseSample {
 double arrival, poseOffset;
 uint32_t device;
 V incoming, outgoing, sceneOffset;
 Q sceneRotation;
};
constexpr unsigned sampleLimit=4096;
PoseSample samples[sampleLimit];
unsigned sampleCount{};
bool captureFinished{};

void process(uint32_t index,vr::DriverPose_t& p,bool capture=false){
 std::lock_guard<std::mutex> guard(poseLock);
 DWORD now=GetTickCount();
 const int cmd=channel->command;
 if(cmd==0 || now-static_cast<DWORD>(channel->heartbeat)>2000){applied={};heldDevice=-1;suppress=false;glideVelocity={};angularVelocity={};gravitySpeed=0;}
 else if(cmd<16){
   if(cmd==3 && heldDevice>=0)applied=beforeHold;
   if(cmd>=0 || (heldDevice>=0 && now-handSample>120)){glideVelocity={};angularVelocity={};gravitySpeed=0;}
   if(cmd<0 && index==0 && p.poseIsValid && p.deviceIsConnected){
     double seconds=(std::min)(now-glideTime,100ul)/1000.0;
     coastRotation(applied,rotationPivot,angularVelocity,channel->spinResistance,seconds);
     coast(applied,glideVelocity,-1-cmd,seconds);
     V normal{channel->floorX,channel->floorY,channel->floorZ};
     Q frame=unit(p.qWorldFromDriverRotation);
     V raw=readVec(p.vecWorldFromDriverTranslation)+rotate(frame,readVec(p.vecPosition));
     for(int i=0;i<channel->wallCount && i<4;++i){
       const auto& wall=channel->walls[i];
       V inward{wall[0],wall[1],wall[2]};
       double approach=dot(glideVelocity,inward);
       double distance=dot(applied.translation+rotate(applied.rotation,raw),inward)-wall[3];
       if(distance<=0 && approach<0){
         double penetration=(std::min)(-distance,-approach*seconds);
         applied.translation=applied.translation+scaled(inward,2*penetration);
         glideVelocity=glideVelocity-scaled(inward,2*approach);
       }
     }
     double acceleration=-9.80665*channel->gravity/100.0;
     applied.translation=applied.translation+scaled(normal,(gravitySpeed+.5*acceleration*seconds)*seconds);
     gravitySpeed+=acceleration*seconds;
     double height=dot(applied.translation+rotate(applied.rotation,raw),normal)-channel->floorOffset;
     double speed=dot(glideVelocity,normal)+gravitySpeed;
     double floor=channel->bounce ? channel->bounceOffset : dot(raw,normal)-channel->floorOffset;
     bool belowFloor=(channel->bounce || channel->gravity>0) && height<=floor && speed<0;
     bool aboveCeiling=channel->ceiling>0 && height>=channel->ceiling && speed>0;
     if(belowFloor || aboveCeiling){
       double boundary=belowFloor ? floor : channel->ceiling;
       bool rebound=aboveCeiling || channel->bounce;
       applied.translation=applied.translation+scaled(normal,boundary-height);
       // Discard overshoot and the extra acceleration accumulated past contact.
       double excess=(std::max)(0.0,2*acceleration*(height-boundary));
       double impactSpeed=std::sqrt((std::max)(0.0,speed*speed-excess));
       double reflection=0;
       if(rebound){
         if(cmd==-1 && impactSpeed>1e-6)reflection=-impactSpeed/std::abs(speed);
         else if(impactSpeed>.12)reflection=-.95*impactSpeed/std::abs(speed);
       }
       glideVelocity=glideVelocity+scaled(normal,(reflection-1)*dot(glideVelocity,normal));
       gravitySpeed*=reflection;
     }
   }
   if(index==0 || heldDevice>=0)glideTime=now;
   heldDevice=-1;suppress=false;
 }
 Q world=unit(p.qWorldFromDriverRotation);
 V translation=readVec(p.vecWorldFromDriverTranslation);
 if(index<64){
   rawPositionTimes[index]=p.poseIsValid && p.deviceIsConnected ? now : 0;
   if(rawPositionTimes[index])rawPositions[index]=translation+rotate(world,readVec(p.vecPosition));
 }
 if(index==0 && p.poseIsValid && p.deviceIsConnected){
   headOrientation=unit(mul(world,p.qRotation)); headset=translation+rotate(world,readVec(p.vecPosition)); headTime=now; channel->active=now;
 }
 if(cmd>=16 && now-static_cast<DWORD>(channel->heartbeat)<=2000 && index==static_cast<unsigned>((cmd-16)%64)){
   if(!p.poseIsValid || !p.deviceIsConnected || !headTime || now-headTime>300){
     heldDevice=-1;suppress=true;glideVelocity={};angularVelocity={};gravitySpeed=0;
   }else if(!suppress){
     Q physical=unit(mul(world,p.qRotation));
          V hand=translation+rotate(world,readVec(p.vecPosition));
     if(heldDevice!=int(index)){initial=physical;beforeHold=applied;heldDevice=int(index);previousHand=hand;glideVelocity={};angularVelocity={};gravitySpeed=0;sampledHandRotation=physical;handSample=now;}
     Q delta=unit(mul(physical,inverse(initial)));
     Transform previous=applied;
     applied.rotation=unit(mul(previous.rotation,delta));
     initial=physical;
     int pivotDevice=channel->pivotDevice-1;
     V pivot=previousHand;
     if(pivotDevice>=0 && pivotDevice<64 && pivotDevice!=int(index) &&
        rawPositionTimes[pivotDevice] && now-rawPositionTimes[pivotDevice]<=300)
       pivot=rawPositions[pivotDevice];
     rotationPivot=pivotDevice>=0 && pivotDevice!=int(index) ? pivot : hand;
     applied.translation=previous.translation+rotate(previous.rotation,pivot)-rotate(applied.rotation,pivot)
       +rotate(applied.rotation,previousHand-hand);
     V pull=rotate(previous.rotation,previousHand-hand);
     double multiplier=((cmd-16)/64+1)*.25;
     if(channel->accelerationSteps>0){
       V velocity=readVec(p.vecVelocity);
       double speed=std::hypot(std::hypot(velocity.x,velocity.y),velocity.z);
       if(std::isfinite(speed))multiplier*=1+.75*(std::min)(speed,3.0)*(channel->accelerationSteps*.25);
     }
     applied.translation=applied.translation+V{pull.x*(multiplier-1),pull.y*(multiplier-1),pull.z*(multiplier-1)};
     pull={pull.x*multiplier,pull.y*multiplier,pull.z*multiplier};
     DWORD elapsed=now-handSample;
     if(elapsed>0){
       double blend=-std::expm1(-static_cast<double>(elapsed)/35.0);
       double scale=1000.0*blend/elapsed;
       V spin=rotate(previous.rotation,angularStep(unit(mul(physical,inverse(sampledHandRotation)))));
       angularVelocity=scaled(angularVelocity,1-blend)+scaled(spin,scale);
       sampledHandRotation=physical;
       handSample=now;
       glideVelocity={glideVelocity.x*(1-blend)+pull.x*scale,glideVelocity.y*(1-blend)+pull.y*scale,glideVelocity.z*(1-blend)+pull.z*scale};
     }
     previousHand=hand;glideTime=now;
   }
 }

 if(index==0 && p.poseIsValid && p.deviceIsConnected){
   InterlockedIncrement(&channel->poseSequence);
   const double values[]={applied.translation.x,applied.translation.y,applied.translation.z,
                          applied.rotation.x,applied.rotation.y,applied.rotation.z,applied.rotation.w};
   for(int i=0;i<7;++i)channel->poseOffset[i]=static_cast<float>(values[i]);
   InterlockedIncrement(&channel->poseSequence);
 }
 if(capture && !captureFinished && cmd>=16 && heldDevice>=0 && sampleCount<sampleLimit &&
    p.deviceIsConnected && p.poseIsValid){
   LARGE_INTEGER tick{},frequency{};
   QueryPerformanceCounter(&tick);
   QueryPerformanceFrequency(&frequency);
   V raw=translation+rotate(world,readVec(p.vecPosition));
   samples[sampleCount++]={double(tick.QuadPart)/frequency.QuadPart,p.poseTimeOffset,index,
     raw,rotate(applied.rotation,raw)+applied.translation,applied.translation,applied.rotation};
 }
 if(applied.rotation.w==1 && applied.rotation.x==0 && applied.rotation.y==0 && applied.rotation.z==0 &&
    applied.translation.x==0 && applied.translation.y==0 && applied.translation.z==0)return;
 if(index==0){
   if(!p.poseIsValid || !p.deviceIsConnected)return;
   Q local=unit(mul(mul(inverse(world),applied.rotation),world));
   p.qRotation=unit(mul(local,p.qRotation));
   V position=rotate(inverse(world),rotate(applied.rotation,headset)+applied.translation-translation);
   p.vecPosition[0]=position.x;p.vecPosition[1]=position.y;p.vecPosition[2]=position.z;
   for(double* vector : {p.vecVelocity,p.vecAcceleration,p.vecAngularVelocity,p.vecAngularAcceleration}){
     V rotated=rotate(local,readVec(vector));
     vector[0]=rotated.x;vector[1]=rotated.y;vector[2]=rotated.z;
   }
   return;
 }
 p.qWorldFromDriverRotation=unit(mul(applied.rotation,world));
 V t=rotate(applied.rotation,translation)+applied.translation;
 p.vecWorldFromDriverTranslation[0]=t.x;p.vecWorldFromDriverTranslation[1]=t.y;p.vecWorldFromDriverTranslation[2]=t.z;
}
template<int Slot> void submit(void* host,uint32_t index,const vr::DriverPose_t& incoming,uint32_t size){
 if(size!=sizeof(vr::DriverPose_t)){original[Slot](host,index,incoming,size);return;}
 auto pose=incoming;
 process(index,pose,true);
 original[Slot](host,index,pose,size);
}
using EyeLayer=vr::IVRDriverDirectModeComponent::SubmitLayerPerEye_t;
using LayerFn=void(*)(void*,const EyeLayer(&)[2]);
using AddFn=bool(*)(void*,const char*,vr::ETrackedDeviceClass,vr::ITrackedDeviceServerDriver*);
LayerFn originalLayer{};
AddFn originalAdd[2]{};
void* layerTarget{};
void* addTargets[2]{};
void* displayComponent{};

V column(const vr::HmdMatrix34_t& m,int i){return {m.m[0][i],m.m[1][i],m.m[2][i]};}
double normSquared(V v){return v.x*v.x+v.y*v.y+v.z*v.z;}
double poseError(const vr::HmdMatrix34_t& pose,Q orientation,V position){
 return normSquared(column(pose,0)-rotate(orientation,{1,0,0}))+
        normSquared(column(pose,1)-rotate(orientation,{0,1,0}))+
        normSquared(column(pose,3)-position);
}
void physicalFrame(vr::HmdMatrix34_t& pose,const Transform& scene){
 for(int i=0;i<4;++i){
   V axis=column(pose,i);
   if(i==3)axis=axis-scene.translation;
   V physical=rotate(inverse(scene.rotation),axis);
   pose.m[0][i]=float(physical.x);pose.m[1][i]=float(physical.y);pose.m[2][i]=float(physical.z);
 }
}
void submitDisplay(void* component,const EyeLayer(&incoming)[2]){
 EyeLayer outgoing[2]={incoming[0],incoming[1]};
 {
   std::lock_guard<std::mutex> guard(poseLock);
   if(component==displayComponent && channel && GetTickCount()-headTime<300){
     Q virtualHead=unit(mul(applied.rotation,headOrientation));
     V virtualPosition=rotate(applied.rotation,headset)+applied.translation;
     for(auto& eye:outgoing){
       double transformed=poseError(eye.mHmdPose,virtualHead,virtualPosition);
       if(transformed<.2){
         channel->displayFrame=GetTickCount();
         if(transformed<poseError(eye.mHmdPose,headOrientation,headset))
           physicalFrame(eye.mHmdPose,applied);
       }
     }
   }
 }
 originalLayer(component,outgoing);
}
HMODULE moduleForAddress(void* address){
 HMODULE module{};
 GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                    reinterpret_cast<LPCWSTR>(address),&module);
 return module;
}

const char* streamingDisplay(HMODULE module){
 if(!module)return nullptr;
 if(module==GetModuleHandleW(L"driver_vrlink.dll"))return "OVR Space Control: Steam Link display pose correction connected";
 if(module==GetModuleHandleW(L"driver_oculus.dll") || module==GetModuleHandleW(L"driver_oculus_legacy.dll"))
   return "OVR Space Control: Oculus display pose correction connected";
 return nullptr;
}

template<int Slot> bool addDevice(void* host,const char* serial,vr::ETrackedDeviceClass type,vr::ITrackedDeviceServerDriver* device){
 bool accepted=originalAdd[Slot](host,serial,type,device);
 if(!accepted || type!=vr::TrackedDeviceClass_HMD || !device)return accepted;

 void* component=device->GetComponent(vr::IVRDriverDirectModeComponent_Version);
 void* callback=component ? (*static_cast<void***>(component))[4] : nullptr;
 const char* driver=streamingDisplay(moduleForAddress(callback));
 bool requiresCorrection=driver || streamingDisplay(moduleForAddress((*reinterpret_cast<void***>(device))[0])) ||
                         (serial && std::strncmp(serial,"VRLINKHMD",9)==0);
 InterlockedExchange(&channel->requiresDisplayCorrection,requiresCorrection ? 1 : 0);
 if(driver && !layerTarget &&
    MH_CreateHook(callback,reinterpret_cast<void*>(submitDisplay),reinterpret_cast<void**>(&originalLayer))==MH_OK){
   displayComponent=component;
   layerTarget=callback;
   if(MH_EnableHook(callback)==MH_OK)vr::VRDriverLog()->Log(driver);
 }
 return accepted;
}
class Provider final:public vr::IServerTrackedDeviceProvider{
 bool initialized=false;
public:
 vr::EVRInitError Init(vr::IVRDriverContext* context) override{
   VR_INIT_SERVER_DRIVER_CONTEXT(context);
   mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(Channel),L"Local\\OVRSpaceControl.Poses.v9");
   if(mapping)channel=static_cast<Channel*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Channel)));
   if(!channel || MH_Initialize()!=MH_OK){Cleanup();return vr::VRInitError_Driver_Failed;}
   initialized=true; channel->command=0; channel->server=0; channel->requiresDisplayCorrection=0;
   const char* versions[]={"IVRServerDriverHost_006","IVRServerDriverHost_005"};
   PoseFn callbacks[]={submit<0>,submit<1>};
   bool installed=false;
   for(int i=0;i<2;++i){
     vr::EVRInitError error{};
     void* host=context->GetGenericInterface(versions[i],&error);
     if(!host)continue;
     // TrackedDevicePoseUpdated is entry 1 in these two specific host interfaces.
     void* entry=(*static_cast<void***>(host))[1];
          void* add=(*static_cast<void***>(host))[0];
     AddFn addCallback=i?addDevice<1>:addDevice<0>;
     if((!i || add!=addTargets[0]) && MH_CreateHook(add,reinterpret_cast<void*>(addCallback),reinterpret_cast<void**>(&originalAdd[i]))==MH_OK){
       addTargets[i]=add;MH_EnableHook(add);
     }
     if(i && entry==targets[0])continue;
     if(MH_CreateHook(entry,reinterpret_cast<void*>(callbacks[i]),reinterpret_cast<void**>(&original[i]))!=MH_OK)continue;
     targets[i]=entry;
     if(MH_EnableHook(entry)==MH_OK)installed=true;
   }
   if(!installed){Cleanup();return vr::VRInitError_Driver_Failed;}
   channel->displayFrame=0;channel->active=0;
   channel->server=GetCurrentProcessId();
   return vr::VRInitError_None;
 }
 void Cleanup() override{
   if(channel)channel->server=0;
   if(initialized){if(layerTarget){MH_DisableHook(layerTarget);MH_RemoveHook(layerTarget);layerTarget=nullptr;}for(auto& t:addTargets)if(t){MH_DisableHook(t);MH_RemoveHook(t);t=nullptr;}for(auto& t:targets)if(t){MH_DisableHook(t);MH_RemoveHook(t);t=nullptr;}MH_Uninitialize();initialized=false;}
   if(channel){UnmapViewOfFile(channel);channel=nullptr;}
   if(mapping){CloseHandle(mapping);mapping=nullptr;}
   VR_CLEANUP_SERVER_DRIVER_CONTEXT();
 }
 const char* const* GetInterfaceVersions()override{return vr::k_InterfaceVersions;}
 void RunFrame()override{
   if(captureFinished)return;
   unsigned count;
   {
     std::lock_guard<std::mutex> guard(poseLock);
     if(!sampleCount || (channel->command>=16 && sampleCount<sampleLimit))return;
     captureFinished=true;
     count=sampleCount;
   }
   wchar_t name[32768];
   HMODULE module=moduleForAddress(reinterpret_cast<void*>(submit<0>));
   DWORD length=GetModuleFileNameW(module,name,32768);
   if(!length || length>=32768)return;
   std::filesystem::path folder(name);
   for(int level=0;level<5;level++)folder=folder.parent_path();
   auto path=folder/L"3d-drag-trace.csv";
   FILE* file=nullptr;
   if(_wfopen_s(&file,path.c_str(),L"w") || !file)return;
   std::fprintf(file,"arrival_s,device,class,pose_offset_s,in_x,in_y,in_z,out_x,out_y,out_z,drag_x,drag_y,drag_z,drag_qw,drag_qx,drag_qy,drag_qz\n");
   int classes[64]{};
   for(unsigned i=0;i<count;i++){
     const auto& v=samples[i];
     if(v.device>=64)continue;
     if(!classes[v.device]){
       auto handle=vr::VRProperties()->TrackedDeviceToPropertyContainer(v.device);
       classes[v.device]=vr::VRProperties()->GetInt32Property(handle,vr::Prop_DeviceClass_Int32);
     }
     std::fprintf(file,"%.7f,%u,%d,%.7f,%.7f,%.7f,%.7f,%.7f,%.7f,%.7f,%.7f,%.7f,%.7f,%.7f,%.7f,%.7f,%.7f\n",
       v.arrival-samples[0].arrival,v.device,classes[v.device],v.poseOffset,
       v.incoming.x,v.incoming.y,v.incoming.z,v.outgoing.x,v.outgoing.y,v.outgoing.z,
       v.sceneOffset.x,v.sceneOffset.y,v.sceneOffset.z,v.sceneRotation.w,v.sceneRotation.x,v.sceneRotation.y,v.sceneRotation.z);
   }
   std::fclose(file);
 }
 bool ShouldBlockStandbyMode()override{return false;}
 void EnterStandby()override{if(channel)channel->command=0;}
 void LeaveStandby()override{}
};
Provider provider;
}
extern "C" __declspec(dllexport) void* HmdDriverFactory(const char* name,int* error){
 if(name && !std::strcmp(name,vr::IServerTrackedDeviceProvider_Version))return &provider;
 if(error)*error=vr::VRInitError_Init_InterfaceNotFound;
 return nullptr;
}
