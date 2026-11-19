import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const source=await readFile(new URL('../Parrot.SharedCompnents/wwwroot/js/avatar-teaching-scene.js',import.meta.url),'utf8');
const {bindTeachingScene}=await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
let checks=0;
const check=(value,why)=>{assert.ok(value,why);checks++;};
function fixture(reduced=false){
  let intersect;
  const rootEvents=new Map(),docEvents=new Map(),motionEvents=new Map(),teacher={};
  const root={dataset:{},offsetWidth:300,querySelector:()=>teacher,addEventListener:(n,f)=>rootEvents.set(n,f),removeEventListener:n=>rootEvents.delete(n)};
  const motion={matches:reduced,addEventListener:(n,f)=>motionEvents.set(n,f),removeEventListener:n=>motionEvents.delete(n)};
  const doc={hidden:false,addEventListener:(n,f)=>docEvents.set(n,f),removeEventListener:n=>docEvents.delete(n)};
  Object.assign(globalThis,{document:doc,matchMedia:()=>motion,IntersectionObserver:class{constructor(f){intersect=f;}observe(){}disconnect(){intersect=null;}}});
  const player=bindTeachingScene(root);
  return {player,root,rootEvents,docEvents,motionEvents,
    visible(v){intersect?.([{isIntersecting:v}]);},hidden(v){doc.hidden=v;docEvents.get('visibilitychange')?.();},
    reduced(v){motion.matches=v;motionEvents.get('change')?.();},end(target=teacher){rootEvents.get('animationend')?.({target});}};
}
let f=fixture();f.player.update(true,0);
check(f.root.dataset.running==='false','The teaching animation does not run below the fold.');
f.visible(true);check(f.root.dataset.running==='true'&&f.root.dataset.playing==='true','An active visible scene starts its finite illustration.');
f.visible(false);check(f.root.dataset.playing==='false'&&f.root.dataset.running==='true','Offscreen pauses all motion at its current pose.');
f.visible(true);check(f.root.dataset.playing==='true','Returning resumes an unfinished scene.');
f.hidden(true);check(f.root.dataset.playing==='false','A hidden tab pauses the scene.');
f.hidden(false);check(f.root.dataset.playing==='true','Returning to the tab resumes.');
f.end({});check(f.root.dataset.playing==='true','A child animation cannot incorrectly finish the scene.');
f.end();check(f.root.dataset.playing==='false','The teacher finishing stops the complete illustration.');
f.visible(false);f.visible(true);check(f.root.dataset.playing==='false','The finished scene holds, without looping on scroll.');
f.player.update(true,0);check(f.root.dataset.playing==='false','An unchanged parent render does not restart a finished scene.');
f.player.update(true,1);check(f.root.dataset.playing==='true','Explicit reselection replays it.');
f.player.update(false,1);check(f.root.dataset.running==='false'&&f.root.dataset.playing==='false','Selecting Review/Update stops hidden teaching motion.');
f.player.update(true,2);check(f.root.dataset.playing==='true','Selecting teaching again starts a fresh illustration.');
f.reduced(true);check(f.root.dataset.running==='false'&&f.root.dataset.playing==='false','Reduced motion immediately produces the default complete still.');
f.reduced(false);check(f.root.dataset.playing==='true','Motion may resume only when the user preference allows it.');
f.player.release();check(f.rootEvents.size===0&&f.docEvents.size===0&&f.motionEvents.size===0,'Disposal removes all observers/listeners.');
f.player.update(true,3);check(f.root.dataset.running==='false','Disposed components cannot restart.');
f=fixture(true);f.visible(true);f.player.update(true,1);check(f.root.dataset.running==='false','Reduced motion at load never autoplays.');f.player.release();
const scene=await readFile(new URL('../Parrot.SharedCompnents/DesignSystem/AvatarTeachingScene.razor',import.meta.url),'utf8');
const css=await readFile(new URL('../Parrot.SharedCompnents/DesignSystem/AvatarTeachingScene.razor.css',import.meta.url),'utf8');
check((scene.match(/Glasses Paused Decorative/g)||[]).length===2,'Both the teacher and learner use glasses on the actual avatar component.');
check(scene.includes('Your business')&&scene.includes('Your AI')&&scene.includes('avatar-teaching__cane'),'The owner teaches the agent with a real pointer illustration.');
check(css.includes('4800ms')&&!css.includes('infinite'),'Motion is a single short pass, not an endless decorative loop.');
check(css.includes('prefers-reduced-motion')&&css.includes('@container (max-width: 300px)'),'The scene has a still mode and a compact-board layout.');
check(!/setInterval|setTimeout|fetch\(|getUserMedia|AudioContext/.test(source),'The binding uses no timer loop, recording, audio or data requests.');
console.log(`${checks} teaching-scene lifecycle/source checks passed.`);
