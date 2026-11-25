const printGuide=document.querySelector('.guide-print');
printGuide.addEventListener('click',()=>window.print());
function updateGuideLanguage(){
 const en=document.documentElement.lang==='en';
 document.querySelector('.guide-download').setAttribute('href',en?'user-guide.en.txt':'user-guide.tr.txt');
 document.querySelector('.guide-toc').setAttribute('aria-label',en?'Guide contents':'Kılavuz içindekiler');
 document.querySelector('.guide-body').setAttribute('aria-label',en?'User guide':'Kullanım kılavuzu');
}
new MutationObserver(updateGuideLanguage).observe(document.documentElement,{attributes:true,attributeFilter:['lang']});
updateGuideLanguage();
