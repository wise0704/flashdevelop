package org.flashdevelop.test.as3.generator.extractlocalvariable {
	import flash.display.Sprite;

	public class ExtractLocalVariable extends Sprite {
		public function ExtractLocalVariable() {
		    // ... some code here ...
			var newVar = getChildByName("child");
			var alpha:Number = newVar.alpha;
			// ... some code here ...
			var name:String = newVar.name;
		}
	}
}