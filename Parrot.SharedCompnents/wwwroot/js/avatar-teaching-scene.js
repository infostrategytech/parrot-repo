// Finite, decorative teaching motion only. No audio, recording, file or workspace access.
export function bindTeachingScene(root) {
  const motion=matchMedia('(prefers-reduced-motion: reduce)');
  const teacher=root.querySelector('[data-teaching-teacher]');
  let active=false,replay=-1,visible=false,started=false,finished=false,disposed=false;
  const reset=()=>{
    root.dataset.running='false';root.dataset.playing='false';
    // One small local style flush restarts CSS motion on explicit reselection; no RAF/timer loop.
    void root.offsetWidth;started=false;finished=false;
  };
  const sync=()=>{
    if(disposed)return;
    const play=active&&visible&&!document.hidden&&!motion.matches&&!finished;
    if(play&&!started){started=true;root.dataset.running='true';}
    root.dataset.playing=String(play);
  };
  const ended=e=>{if(e.target===teacher){finished=true;sync();}};
  const changed=()=>{if(motion.matches)reset();sync();};
  const observer=new IntersectionObserver(([entry])=>{visible=entry.isIntersecting;sync();},{threshold:.3});
  root.dataset.running='false';root.dataset.playing='false';
  root.addEventListener('animationend',ended);document.addEventListener('visibilitychange',sync);
  motion.addEventListener('change',changed);observer.observe(root);
  return {
    update(nextActive,nextReplay){
      if(disposed||active===nextActive&&replay===nextReplay)return;
      active=nextActive;replay=nextReplay;reset();sync();
    },
    release(){disposed=true;observer.disconnect();root.removeEventListener('animationend',ended);document.removeEventListener('visibilitychange',sync);motion.removeEventListener('change',changed);root.dataset.running='false';root.dataset.playing='false';}
  };
}
