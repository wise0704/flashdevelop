﻿package;

import common.Test;
#if flash
import flash.FlashSpecific;
#elseif js
import js.JsSpecfic;
#endif
import utils.SomeHelper;

class Main {
	public function new() {
		$(EntryPoint)
	}
}