	 //Write a cookie, giving it a name to access it, a value to hold, and a 
	 //number of days until expiration
	 function writeCookie(name, value, days) {
        if (days) {
            var date = new Date();
            date.setTime(date.getTime()+(days*24*60*60*1000));
            var expires = "; expires="+date.toGMTString();
            }
            else var expires = "";    
            document.cookie = name+"="+value+expires+"; path=/";
    }
    
	 //Read the value of a cookie with a given name
function readCookie(name) {
	var nameEQ = name + "=";
	var ca = document.cookie.split(';');
	for(var i=0;i < ca.length;i++) {
		var c = ca[i];
		while (c.charAt(0)==' ') c = c.substring(1,c.length);
		if (c.indexOf(nameEQ) == 0) return c.substring(nameEQ.length,c.length);
	}
	return null;
}
     
    //Delete the cookie with the given name.
    function deleteCookie(name) {
        writeCookie(name,"",-1);
    }