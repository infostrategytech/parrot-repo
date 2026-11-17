// Controlled DOM/geometry fakes, not customer fixtures, demo services or live APIs.
import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const source=await readFile(new URL('../Parrot.SharedCompnents/wwwroot/js/aurora-interop.js',import.meta.url),'utf8');
const {reserveUnder,scrollToBottom}=await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function surface(){
 const frames=new Map(),observers=[];let next=0;
 class Element{
  constructor(){this.children=[];this.listeners=new Map();this.values=new Map();this.natural=1200;this.clientHeight=900;this.top=0;this.bottom=900;this.height=900;this._scrollTop=0;
   this.style={getPropertyValue:key=>this.values.get(key)??'',setProperty:(key,value)=>this.values.set(key,value),removeProperty:key=>this.values.delete(key)};}
  get firstElementChild(){return this.children[0];}
  get scrollHeight(){return Math.max(this.clientHeight,this.natural+(parseFloat(this.style.getPropertyValue('--reserve'))||0));}
  get scrollTop(){return this._scrollTop;}
  set scrollTop(value){this._scrollTop=Math.max(0,Math.min(value,this.scrollHeight-this.clientHeight));}
  getBoundingClientRect(){return {top:this.top,bottom:this.bottom,height:this.height};}
  addEventListener(name,callback){this.listeners.set(name,callback);}
  removeEventListener(name){this.listeners.delete(name);}
  fire(name,event={}){this.listeners.get(name)?.(event);}
 }
 globalThis.Element=Element;
 globalThis.getComputedStyle=()=>({rowGap:'8px'});
 globalThis.requestAnimationFrame=callback=>{frames.set(++next,callback);return next;};
 globalThis.cancelAnimationFrame=id=>frames.delete(id);
 globalThis.ResizeObserver=class{
  constructor(callback){this.callback=callback;this.observed=[];this.stopped=false;observers.push(this);}
  observe(element){this.observed.push(element);}
  disconnect(){this.stopped=true;}
 };
 const scroller=new Element(),messages=new Element(),footer=new Element(),card=new Element();
 scroller.children=[messages];footer.children=[card];card.top=640;card.height=80;footer.top=600;
 scroller.scrollTop=scroller.scrollHeight;
 const handle=reserveUnder(scroller,footer,'--reserve');
 const flush=()=>{const callbacks=[...frames.values()];frames.clear();callbacks.forEach(callback=>callback());};
 const resize=()=>observers[0].callback();
 flush();
 return {scroller,footer,card,handle,flush,resize,observer:observers[0]};
}

test('Actual first card controls the reserve, including growth and shrink',()=>{
 const s=surface();assert.equal(s.scroller.style.getPropertyValue('--reserve'),'276px');
 s.card.top=540;s.resize();s.flush();assert.equal(s.scroller.style.getPropertyValue('--reserve'),'376px');
 s.card.top=700;s.resize();s.flush();assert.equal(s.scroller.style.getPropertyValue('--reserve'),'216px');
 assert.equal(s.scroller.scrollTop,s.scroller.scrollHeight-s.scroller.clientHeight);s.handle.release();
});
test('Reading older messages survives footer changes and non-forced arrivals',()=>{
 const s=surface();s.scroller.scrollTop=100;s.scroller.fire('scroll');
 s.card.top=540;s.scroller.natural+=200;s.resize();scrollToBottom(s.scroller,false);s.flush();
 assert.equal(s.scroller.scrollTop,100);s.handle.release();
});
test('Pinned content growth follows the latest message',()=>{
 const s=surface();s.scroller.natural+=200;s.resize();s.flush();
 assert.equal(s.scroller.scrollTop,s.scroller.scrollHeight-s.scroller.clientHeight);s.handle.release();
});
test('Returning to the end resumes following; explicit first-open can force it',()=>{
 const s=surface();s.scroller.scrollTop=100;s.scroller.fire('scroll');scrollToBottom(s.scroller,true);s.flush();
 assert.equal(s.scroller.scrollTop,s.scroller.scrollHeight-s.scroller.clientHeight);
 s.scroller.fire('scroll');s.card.top=550;s.resize();s.flush();
 assert.equal(s.scroller.scrollTop,s.scroller.scrollHeight-s.scroller.clientHeight);s.handle.release();
});
test('An upward wheel cancels a queued follow frame',()=>{
 const s=surface();scrollToBottom(s.scroller,true);s.scroller.fire('wheel',{deltaY:-100});s.scroller.scrollTop=100;s.flush();
 assert.equal(s.scroller.scrollTop,100);s.handle.release();
});
test('Releasing the binding removes observers, listeners and its reserve',()=>{
 const s=surface();assert.equal(s.observer.observed.length,3);s.handle.release();
 assert.ok(s.observer.stopped);assert.equal(s.scroller.listeners.size,0);
 assert.equal(s.scroller.__auroraReservedEnd,undefined);assert.equal(s.scroller.style.getPropertyValue('--reserve'),'');
});
