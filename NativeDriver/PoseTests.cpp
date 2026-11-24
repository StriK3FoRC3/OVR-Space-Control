// Exercise the same pose processing used by the SteamVR provider without registering it.
#include "Driver.cpp"
#include <cstdio>

bool approximatelyEqual(double expected,double actual){return std::abs(expected-actual)<1e-8;}
int main(){
 Channel local{};
 channel=&local;
 local.heartbeat=GetTickCount();
 vr::DriverPose_t head{};
 head.deviceIsConnected=head.poseIsValid=true;
 head.qRotation=head.qWorldFromDriverRotation={1,0,0,0};
 head.vecPosition[0]=2;head.vecPosition[1]=1;head.vecPosition[2]=3;
 auto hand=head;
 hand.vecPosition[1]=0;
 process(0,head);
 local.command=17+3*64;
 process(1,hand);
 double k=std::sqrt(.5);
 hand.qRotation={k,k,0,0};
 process(1,hand);
 auto pitched=head;
 process(0,pitched);
 if(!approximatelyEqual(pitched.qRotation.x,k))return 1;
 auto pivot=rotate(pitched.qWorldFromDriverRotation,readVec(pitched.vecPosition))+readVec(pitched.vecWorldFromDriverTranslation);
 if(!approximatelyEqual(pivot.x,2)||!approximatelyEqual(pivot.y,0)||!approximatelyEqual(pivot.z,4))return 2;
 local.command=2;
 auto keep=head;process(0,keep);
 if(!approximatelyEqual(keep.qRotation.x,k))return 3;
 local.command=17+3*64;
 hand=head;process(1,hand);
 hand=head;hand.qRotation={k,0,0,k};process(1,hand);
 auto rolled=head;process(0,rolled);
 if(!approximatelyEqual(rolled.qRotation.z,.5))return 4;
 local.command=3;
 auto cancelled=head;process(0,cancelled);
 if(!approximatelyEqual(cancelled.qRotation.x,k)||!approximatelyEqual(cancelled.qRotation.z,0))return 5;
 local.command=0;
 auto reset=head;process(0,reset);
 if(!approximatelyEqual(reset.qRotation.w,1)||!approximatelyEqual(reset.vecWorldFromDriverTranslation[1],0))return 6;
 for(int selected : {0,2}){
   auto fixedPose=head;
   if(selected==2)fixedPose.vecPosition[1]+=2;
   process(selected,fixedPose);
   V fixedPoint=readVec(fixedPose.vecPosition);
   local.pivotDevice=selected+1;
   local.command=17+3*64;
   hand=head;hand.vecPosition[1]=0;process(1,hand);
   hand.qRotation={k,k,0,0};process(1,hand);
   V result=rotate(applied.rotation,fixedPoint)+applied.translation;
   if(!approximatelyEqual(result.x,fixedPoint.x)||!approximatelyEqual(result.y,fixedPoint.y)||
      !approximatelyEqual(result.z,fixedPoint.z))return 70+selected;
   local.command=0;
   auto clean=head;process(0,clean);
 }
 local.pivotDevice=0;
 vr::HmdMatrix34_t frame={{{0,-1,0,4},{1,0,0,3},{0,0,1,6}}};
 physicalFrame(frame,Transform{{k,0,0,k},{5,1,3}});
 if(poseError(frame,{1,0,0,0},{2,1,3})>1e-8)return 7;
 Transform glide{}; V speed{2,0,0};
 coast(glide,speed,0,1);if(!approximatelyEqual(glide.translation.x,2)||speed.x!=2)return 8;
 coast(glide,speed,50,1);if(!approximatelyEqual(speed.x,1))return 9;
 auto stop=glide.translation;coast(glide,speed,100,1);if(speed.x!=0||glide.translation.x!=stop.x)return 10;
 local.command=17+3*64;hand=head;process(1,hand);hand=head;hand.vecPosition[0]+=1;process(1,hand);
 if(!approximatelyEqual(applied.translation.x,-1))return 11;
 local.command=2;auto released=head;process(0,released);
 if(!approximatelyEqual(applied.translation.x,-1))return 12;
 local.command=17+7*64;hand=head;process(1,hand);hand=head;hand.vecPosition[0]+=1;process(1,hand);
 if(!approximatelyEqual(applied.translation.x,-3))return 13;
 // A real interval between samples exercises the velocity filter used on release.
 hand=head;hand.vecPosition[0]+=1.1;handSample=GetTickCount()-10;process(1,hand);
 if(!std::isfinite(glideVelocity.x)||glideVelocity.x>=0||glideVelocity.x < -21)return 14;
 local.command=-1;glideTime=GetTickCount()-10;auto moving=head;process(0,moving);
 if(!std::isfinite(moving.vecPosition[0])||applied.translation.x>=-3.2)return 15;
 auto turned=head;turned.qRotation={k,0,k,0};process(0,turned);
 if(!approximatelyEqual(turned.qRotation.y,k))return 16;
 puts("Timed R3 release preserves finite glide and live head tracking.");
 for(V axis : {V{0,1,0},V{0,0,1},V{1,0,0}}){
   Transform turn{};V spin=axis;V center{2,1,3};
   coastRotation(turn,center,spin,0,1);
   if(normSquared(angularStep(turn.rotation)-axis)>1e-8 || normSquared(spin-axis)>1e-8)return 17;
   if(normSquared(rotate(turn.rotation,center)+turn.translation-center)>1e-8)return 18;
   coastRotation(turn,center,spin,50,1);
   if(normSquared(spin-scaled(axis,.5))>1e-8)return 19;
 }
 local.command=0;auto restored=head;process(0,restored);
 if(normSquared(applied.translation)>1e-8 || applied.rotation.w!=1 || normSquared(angularVelocity)>0)return 20;
 local.command=17+3*64;local.accelerationSteps=4;hand=head;process(1,hand);
 hand=head;hand.vecPosition[0]+=1;hand.vecVelocity[0]=2;process(1,hand);
 if(!approximatelyEqual(applied.translation.x,-2.5))return 21;
 puts("Accelerated 3D drag passed.");
 applied={};local.command=-1;heldDevice=-1;glideVelocity={1,-2,0};angularVelocity={0,1,0};
 local.bounce=1;local.floorY=1;local.floorOffset=1;local.spinResistance=100;
 glideTime=GetTickCount()-50;auto impact=head;process(0,impact);
 if(!approximatelyEqual(impact.vecPosition[1],1) || !approximatelyEqual(glideVelocity.y,2) || glideVelocity.x!=1 || normSquared(angularVelocity)!=0)return 22;
 local.command=-101;local.spinResistance=0;angularVelocity={0,0,1};
 glideTime=GetTickCount()-50;auto spinning=head;process(0,spinning);
 if(normSquared(glideVelocity)!=0 || angularVelocity.z!=1 || spinning.qRotation.z<=0)return 23;
 // Reverse gravity accelerates upward even with movement resistance at 100%.
 applied={};local.bounce=0;local.floorOffset=0;local.gravity=-50;local.ceiling=0;
 local.spinResistance=100;local.command=-101;gravitySpeed=0;
 glideTime=GetTickCount()-50;auto rising=head;process(0,rising);
 if(rising.vecPosition[1]<=head.vecPosition[1] || gravitySpeed<=0)return 24;
 local.gravity=0;local.ceiling=1;
 glideTime=GetTickCount()-50;auto rebounding=head;process(0,rebounding);
 if(!approximatelyEqual(rebounding.vecPosition[1],1) || gravitySpeed>=0)return 25;
 local.ceiling=0;local.gravity=100;applied.translation.y=1;gravitySpeed=0;
 glideTime=GetTickCount()-50;auto falling=head;process(0,falling);
 if(falling.vecPosition[1]>=2 || falling.vecPosition[1]<=1)return 26;
 puts("Positive gravity, negative gravity and ceiling rebound passed.");
 applied={};local.ceiling=0;local.gravity=0;local.wallCount=1;
 local.walls[0][0]=1;local.walls[0][3]=2;
 local.command=-1;gravitySpeed=0;glideVelocity={-2,0,1};angularVelocity={};
 glideTime=GetTickCount()-50;auto wallImpact=head;process(0,wallImpact);
 if(wallImpact.vecPosition[0]<=2 || glideVelocity.x!=2 || glideVelocity.z!=1)return 27;
 puts("Wall reflection preserves tangential glide.");
 applied={};local.wallCount=0;local.gravity=100;local.bounce=1;local.floorOffset=1;
 glideVelocity={};gravitySpeed=0;
 for(int frame=0;frame<8;++frame){
   glideTime=GetTickCount()-50;
   auto resting=head;process(0,resting);
   if(!approximatelyEqual(resting.vecPosition[1],1) || !approximatelyEqual(gravitySpeed,0))return 28;
 }
 puts("Gravity does not create a rebound at rest.");
 puts("Floor rebound and independent movement and rotation resistance passed.");
 puts("Angular coast, resistance, controller pivot and reset passed.");
 puts("Combined drag and release resistance tests passed.");
 puts("Streamed frame roll and pivot translation restored to physical headset pose.");
 puts("Native pose tests passed: pitch, roll, offset controller pivot, release, cancellation, reset.");
}
