// Shared static product-page navigation, translation and catalog search.
const bilingualNodes=[...document.querySelectorAll('[data-copy-tr]')];
const standalone=Boolean(document.body.dataset.titleTr);
let moduleFilter='all';
const search=document.getElementById('feature-search');
const fold=value=>value.toLocaleLowerCase('tr').replaceAll('ı','i').normalize('NFD').replace(/[\u0300-\u036f]/g,'');
function updateCatalog(){
 if(!search)return;
 const query=fold(search.value.trim());let visible=0;
 document.querySelectorAll('.catalog-chapter').forEach(chapter=>{
  const selected=moduleFilter==='all'||moduleFilter===chapter.dataset.module;
  let matches=0;
  chapter.querySelectorAll('.catalog-feature').forEach(feature=>{
   const shown=selected&&(!query||fold(feature.dataset.search).includes(query));
   feature.hidden=!shown;if(shown)matches++;
  });
  chapter.hidden=matches===0;visible+=matches;
 });
 document.querySelectorAll('[data-module-filter]').forEach(button=>{
  const active=button.dataset.moduleFilter===moduleFilter;
  button.setAttribute('aria-pressed',String(active));button.classList.toggle('is-current',active);
 });
 document.getElementById('catalog-result').textContent=document.documentElement.lang==='en'?`${visible} capability explanation${visible===1?'':'s'} shown`:`${visible} özellik açıklaması gösteriliyor`;
 document.querySelector('.catalog-empty').hidden=visible!==0;
}
function translateProduct(){
 const language=document.documentElement.lang==='en'?'en':'tr';
 bilingualNodes.forEach(node=>{node.textContent=language==='en'?node.dataset.copyEn:node.dataset.copyTr;});
 if(search){search.placeholder=language==='en'?search.dataset.placeholderEn:search.dataset.placeholderTr;}
 if(standalone){
  document.title=(language==='en'?document.body.dataset.titleEn:document.body.dataset.titleTr)+' — DevWorkspace';
  const toggle=document.getElementById('language');toggle.textContent=language==='en'?'TR':'EN';toggle.setAttribute('aria-label',language==='en'?'Türkçeye geç':'Switch to English');
  document.getElementById('navigation').setAttribute('aria-label',language==='en'?'Main navigation':'Ana menü');
  const menu=document.getElementById('menu-toggle');const open=menu.getAttribute('aria-expanded')==='true';menu.setAttribute('aria-label',language==='en'?(open?'Close menu':'Open menu'):(open?'Menüyü kapat':'Menüyü aç'));
 }
 updateCatalog();
}
if(standalone){
 try{if(localStorage.getItem('devworkspace-site-language')==='en')document.documentElement.lang='en';}catch{}
 const toggle=document.getElementById('language');
 toggle.addEventListener('click',()=>{document.documentElement.lang=document.documentElement.lang==='en'?'tr':'en';try{localStorage.setItem('devworkspace-site-language',document.documentElement.lang);}catch{}translateProduct();});
 const menu=document.getElementById('menu-toggle');const nav=document.getElementById('navigation');
 const closeMenu=()=>{nav.classList.remove('is-open');menu.setAttribute('aria-expanded','false');translateProduct();};
 menu.addEventListener('click',()=>{const open=menu.getAttribute('aria-expanded')!=='true';menu.setAttribute('aria-expanded',String(open));nav.classList.toggle('is-open',open);translateProduct();});
 nav.addEventListener('click',e=>{if(e.target.closest('a'))closeMenu();});
 document.addEventListener('click',e=>{if(!e.target.closest('.header')&&menu.getAttribute('aria-expanded')==='true')closeMenu();});
 document.addEventListener('keydown',e=>{if(e.key==='Escape'&&menu.getAttribute('aria-expanded')==='true'){closeMenu();menu.focus();}});
}
search?.addEventListener('input',updateCatalog);
document.querySelectorAll('[data-module-filter]').forEach(button=>button.addEventListener('click',()=>{moduleFilter=button.dataset.moduleFilter;updateCatalog();}));
document.querySelector('.catalog-reset')?.addEventListener('click',()=>{search.value='';moduleFilter='all';updateCatalog();search.focus();});
new MutationObserver(translateProduct).observe(document.documentElement,{attributes:true,attributeFilter:['lang']});
translateProduct();
